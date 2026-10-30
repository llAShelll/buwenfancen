using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BuWenFanChen;

/// <summary>
/// 自动好感度判定（2026-10-27 主人要，测试功能）。
///
/// 背景：游戏/TTS 的 JSON 契约把输出字段锁死了，模型不肯多吐一个 intimacy_delta，
/// 所以改成「把这一轮对话单独发给 DeepSeek，让它只回一个数字」，再调游戏 AddIntimacy。
///
/// 配置（PromptConfig）：
///   AutoIntimacyEnabled / AutoIntimacyMaxUp / AutoIntimacyMaxDown / AutoIntimacyMode
/// 生效方式：
///   async —— 后台异步判断，下一轮主线程应用（迟一轮）
///   sync  —— 同步判断，当前轮立刻应用（退出对话前最后一轮不生效）
///
/// 安全：调游戏 AddIntimacy 永远在有 npc 对象的主线程执行，不跨线程碰 Il2Cpp 对象。
/// </summary>
public static class IntimacyJudge
{
    private static readonly Dictionary<string, int> _pending = new();
    private static readonly object _lock = new();
    private static bool _busy;

    // ★ 2026-10-27：控制台统计表格用——每轮判断出来的 delta（npcId → 列表）
    private static readonly Dictionary<string, List<int>> _history = new();
    private static readonly object _histLock = new();
    private static string _lastNpc;

    /// <summary>最近一次参与判定的 NPC（控制台表格默认显示这个）。</summary>
    public static string LastNpc { get { try { lock (_histLock) return _lastNpc; } catch { return null; } } }

    /// <summary>取某个 NPC 的历史好感变化（每轮一个值，0 表示没调整）。</summary>
    public static List<int> GetHistory(string cfgId)
    {
        try
        {
            lock (_histLock)
                return (!string.IsNullOrEmpty(cfgId) && _history.TryGetValue(cfgId, out var l))
                    ? new List<int>(l) : new List<int>();
        }
        catch { return new List<int>(); }
    }

    private static void RecordDelta(string cfgId, int delta)
    {
        try
        {
            lock (_histLock)
            {
                _lastNpc = cfgId;
                if (!_history.TryGetValue(cfgId, out var l)) { l = new List<int>(); _history[cfgId] = l; }
                l.Add(delta);
                if (l.Count > 500) l.RemoveRange(0, l.Count - 500);
            }
        }
        catch { }
    }

    /// <summary>响应侧调用：按配置走同步/异步。</summary>
    /// <param name="npc">响应侧的 NpcModel 对象（sync 模式当场 AddIntimacy 用）。</param>
    public static void Judge(string cfgId, string playerText, string npcText, object npc)
    {
        try
        {
            if (!PromptConfig.AutoIntimacyEnabled) return;
            if (string.IsNullOrEmpty(cfgId)) return;
            if (PromptConfig.AutoIntimacyMode == "sync")
                JudgeSync(cfgId, playerText, npcText, npc);
            else
                JudgeAsync(cfgId, playerText, npcText);
        }
        catch { }
    }

    // ============ 判定用的 API 配置 ============
    /// <summary>
    /// 好感判断的 API：填了 autoIntimacyApiUrl 就用它（本地 Ollama 等），否则沿用主对话 API。
    /// 本地推理不需要真 key，填个占位即可。
    /// </summary>
    private static BuWenFanChen.Memory.LlmClient.Config GetJudgeConfig()
    {
        try
        {
            var url = (PromptConfig.AutoIntimacyApiUrl ?? "").Trim();
            if (url.Length > 0)
            {
                var key = (PromptConfig.AutoIntimacyApiKey ?? "").Trim();
                return new BuWenFanChen.Memory.LlmClient.Config
                {
                    Url = BuWenFanChen.Memory.LlmClient.NormalizeUrl(url),
                    // 本地 Ollama 不需要真 key，填个占位；填了云 API 的 key 就用真的
                    ApiKey = key.Length > 0 ? key : "ollama",
                    Model = string.IsNullOrWhiteSpace(PromptConfig.AutoIntimacyModel)
                        ? "qwen2.5:0.5b"
                        : PromptConfig.AutoIntimacyModel.Trim(),
                };
            }
        }
        catch { }
        return BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
    }

    // ============ 同步：当前轮立刻生效 ============
    private static void JudgeSync(string cfgId, string playerText, string npcText, object npc)
    {
        try
        {
            var cfg = GetJudgeConfig();
            if (cfg == null || !cfg.IsValid) { Log("[好感] 拿不到 API 配置，跳过"); return; }

            var p = Clip(playerText, 500);
            var n = Clip(npcText, 800);
            var (content, err) = BuWenFanChen.Memory.LlmClient.CompleteAsync(
                cfg, PromptConfig.BuildAutoIntimacyPrompt(), "玩家：" + p + "\n角色：" + n, 16)
                .GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(content)) { Log($"[好感] 判断 API 失败：{err}"); return; }
            if (!TryParseDelta(content, out var d))
            {
                Log($"[好感] 判断结果不是数字：「{content.Trim()}」");
                return;
            }
            RecordDelta(cfgId, d);
            ApplyToGame(npc, d);
        }
        catch (Exception e)
        {
            Log($"[好感] 同步判断异常：{e.GetType().Name}: {e.Message}");
        }
    }

    // ============ 异步：迟一轮 ============
    private static void JudgeAsync(string cfgId, string playerText, string npcText)
    {
        try
        {
            if (_busy) { Log("[好感] 上一轮判断还没结束，本轮跳过"); return; }
            _busy = true;

            var p = Clip(playerText, 500);
            var n = Clip(npcText, 800);
            Log($"[好感] 开始异步判断（npc={cfgId}，玩家 {p.Length} 字 / 角色 {n.Length} 字）");

            Task.Run(async () =>
            {
                try
                {
                    var cfg = GetJudgeConfig();
                    if (cfg == null || !cfg.IsValid) { Log("[好感] 拿不到 API 配置，跳过"); return; }
                    var (content, err) = await BuWenFanChen.Memory.LlmClient.CompleteAsync(
                        cfg, PromptConfig.BuildAutoIntimacyPrompt(), "玩家：" + p + "\n角色：" + n, 16);
                    if (string.IsNullOrWhiteSpace(content)) { Log($"[好感] 判断 API 失败：{err}"); return; }
                    if (!TryParseDelta(content, out var d))
                    {
                        Log($"[好感] 判断结果不是数字：「{content.Trim()}」");
                        return;
                    }
                    RecordDelta(cfgId, d);
                    if (d == 0) { Log("[好感] 判断为 0，不调整"); return; }
                    lock (_lock) _pending[cfgId] = d;
                    Log($"[好感] 判断结果 {d:+0;-0} 已存，等下一轮应用");
                }
                catch (Exception e)
                {
                    Log($"[好感] 异步判断异常：{e.GetType().Name}: {e.Message}");
                }
                finally { _busy = false; }
            });
        }
        catch { }
    }

    /// <summary>请求侧（主线程）取走上一轮判断出来的 delta（取走即清空）。</summary>
    public static int? TakePending(string cfgId)
    {
        try
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(cfgId) && _pending.TryGetValue(cfgId, out var d))
                {
                    _pending.Remove(cfgId);
                    return d;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>调游戏的 NpcModel.AddIntimacy（必须在有 npc 对象的主线程调用）。</summary>
    public static void ApplyToGame(object npc, int delta)
    {
        try
        {
            if (npc == null || delta == 0) return;
            delta = Math.Clamp(delta, -Math.Clamp(PromptConfig.AutoIntimacyMaxDown, 1, 20), Math.Clamp(PromptConfig.AutoIntimacyMaxUp, 1, 20));
            var m = npc.GetType().GetMethod("AddIntimacy");
            if (m == null) { Log("[好感] 找不到 AddIntimacy 方法"); return; }
            var ps = m.GetParameters();
            object[] args = ps.Length >= 2
                ? new object[] { delta, Enum.ToObject(ps[1].ParameterType, 0) }
                : new object[] { delta };
            m.Invoke(npc, args);

            var cfgId = BuWenFanChen.Memory.MemoryEngine.GetNpcCfgId(npc);
            Log($"[好感] LLM 判断 {delta:+0;-0}（npc={cfgId}）→ 已调用 AddIntimacy");
        }
        catch (Exception e)
        {
            Log($"[好感] 应用失败：{e.GetType().Name}: {e.Message}");
        }
    }

    private static bool TryParseDelta(string content, out int delta)
    {
        delta = 0;
        try
        {
            var s = content.Trim().TrimStart('+').Trim();
            if (int.TryParse(s, out var d))
            {
                delta = Math.Clamp(d, -Math.Clamp(PromptConfig.AutoIntimacyMaxDown, 1, 20), Math.Clamp(PromptConfig.AutoIntimacyMaxUp, 1, 20));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    private static void Log(string msg)
    {
        try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(msg); } catch { }
    }
}
