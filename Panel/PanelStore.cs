namespace BuWenFanChen.Panel;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BuWenFanChen.Memory;
using BepInEx.Logging;

/// <summary>
/// ★ 2026-11-12：NPC 数值面板 · 状态（1.4.0 P0）
/// 施工图：`施工_NPC数值面板_20261112.md` §1.5 / §2 / §3
/// 原则：面板只影响「提示词表达」，不改游戏数据；默认 panelEnabled=false 时零行为变化。
/// </summary>
internal sealed class PanelState
{
    public string NpcId = "";
    public string NpcName = "";

    /// <summary>总轮数（四轴衰减计时基准）</summary>
    public int Rounds;

    /// <summary>距上次亲密场景的空闲轮数（NSFW 四项衰减计时基准）</summary>
    public int NsfwIdleRounds;

    public bool Locked;

    /// <summary>keyword / llm / manual / template / default</summary>
    public string InitSource = "default";

    /// <summary>NSFW 四项的基线：submission / shame / openness / corruption（衰减朝这里回）</summary>
    public int[] Base = new int[4];

    /// <summary>8 个当前值：warm / attitude / initiative / verbose | submission / shame / openness / corruption</summary>
    public int[] Values = new int[8];

    public int[] Cooldown = new int[8];
    public int[] Streak = new int[8];

    /// <summary>最近 200 轮的非零变化（只用于 UI 曲线 / 成长预览）</summary>
    public List<(int Round, int[] Delta)> History = new();

    public string UpdatedAt = "";
}

/// <summary>数值面板的存储与应用（P0：不带 UI / 不带推断）。</summary>
internal static class PanelStore
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·面板");
    private static readonly object _lock = new();

    /// <summary>每个角色的连续解析失败计数</summary>
    private static readonly Dictionary<string, int> _fail = new();

    /// <summary>连续失败 → 本局暂停（内存生效，不改 config）</summary>
    private static bool _paused;

    private static DateTime _lastWarn = DateTime.MinValue;

    private static readonly string[] Keys =
        { "warm", "attitude", "initiative", "verbose", "submission", "shame", "openness", "corruption" };

    private static readonly string[] Cn =
        { "温度", "态度", "主动", "话量", "服从", "羞耻", "开放", "崩坏" };

    private const int NsfwBaseSubmission = 30;
    private const int NsfwBaseShame = 60;
    private const int NsfwBaseOpenness = 30;
    private const int NsfwBaseCorruption = 0;

    /// <summary>★ 2026-11-12：最近一次对话的 NPC（HUD 打开时显示谁的面板）</summary>
    internal static string LastNpcId { get; private set; } = "";

    /// <summary>★ 2026-11-13：最近一轮实际生效的 delta（8 项，面板显示 ↑/↓/─ 箭头用；重启清零）</summary>
    internal static int[] LastDelta;
    internal static string LastDeltaNpc = "";

    // ★ 2026-11-13 主人定（B 方案）：性格面板只服务女性角色——男性 NPC 不显示面板、不注入两段
    //   读不到性别时返回 false（面板照常，避免误伤）
    private static readonly Dictionary<string, string> _genderCache = new();

    /// <summary>当前正在对话的 NPC（每轮 tail 生成时写入；面板按钮用它判断性别）</summary>
    internal static string CurrentNpc = "";
    internal static void NoteCurrentNpc(string npcId)
    {
        try { if (!string.IsNullOrEmpty(npcId)) CurrentNpc = npcId; } catch { }
    }

    /// <summary>是否对该 NPC 隐藏面板：男性 NPC 且没开「男角色也能用面板」→ 隐藏</summary>
    internal static bool ShouldHidePanel(string npcId)
    {
        try { return IsMaleNpc(npcId) && !PromptConfig.PanelForMale; }
        catch { return false; }
    }

    internal static bool IsMaleNpc(string npcId)
    {
        try
        {
            if (string.IsNullOrEmpty(npcId)) return false;
            string g;
            lock (_genderCache)
            {
                if (!_genderCache.TryGetValue(npcId, out g))
                {
                    g = BuWenFanChen.Memory.ConfigTable.GetGender(npcId);
                    _genderCache[npcId] = g;
                    Log.LogInfo($"[面板] 性别探测 npc={npcId} → {(string.IsNullOrEmpty(g) ? "没读到（面板照常）" : g)}");
                }
            }
            return g == "男";
        }
        catch { return false; }
    }
    internal static string LastNpcName { get; private set; } = "";

    /// <summary>每次收到模型回复后更新（面板 HUD 用）</summary>
    internal static void NoteActiveNpc(string npcId)
    {
        if (string.IsNullOrEmpty(npcId)) return;
        LastNpcId = npcId;
    }

    // ================= 读写 =================

    private static PanelState NewDefault(string npcId) => PanelInit.DefaultState(npcId);

    internal static PanelState Load(string npcId)
    {
        if (string.IsNullOrEmpty(npcId)) return NewDefault("");
        var st = NewDefault(npcId);
        try
        {
            var path = MemoryPaths.PanelPath(npcId);
            if (!File.Exists(path))
            {
                // ★ P1（§5.1）：第一次见到这个角色 → 本地关键词推断（零 LLM 成本），并落盘固化 base。
                //   以后不再自动重算（只有 [重置本角色] / [按角色卡重新推断] 才改）。
                var fresh = PanelInit.InitByKeywords(npcId);
                Save(fresh);
                return fresh;
            }

            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject o)
                return st;

            st.NpcId = Str(o["npcId"]) ?? st.NpcId;
            st.NpcName = Str(o["npcName"]) ?? "";
            st.Rounds = Math.Max(0, Int(o["rounds"]));
            st.NsfwIdleRounds = Math.Max(0, Int(o["nsfwIdleRounds"]));
            st.Locked = Bool(o["locked"]);
            st.InitSource = Str(o["initSource"]) ?? "default";
            st.UpdatedAt = Str(o["updatedAt"]) ?? "";

            if (o["base"] is JsonObject b)
            {
                st.Base[0] = Math.Clamp(Int(b["submission"], NsfwBaseSubmission), 0, 100);
                st.Base[1] = Math.Clamp(Int(b["shame"], NsfwBaseShame), 0, 100);
                st.Base[2] = Math.Clamp(Int(b["openness"], NsfwBaseOpenness), 0, 100);
                st.Base[3] = Math.Clamp(Int(b["corruption"], NsfwBaseCorruption), 0, 100);
            }

            if (o["values"] is JsonObject v)
            {
                for (int i = 0; i < 4; i++) st.Values[i] = Math.Clamp(Int(v[Keys[i]], 0), -100, 100);
                for (int i = 4; i < 8; i++) st.Values[i] = Math.Clamp(Int(v[Keys[i]], st.Base[i - 4]), 0, 100);
            }
            else
            {
                for (int i = 0; i < 4; i++) st.Values[4 + i] = st.Base[i];
            }

            if (o["cooldown"] is JsonObject cd)
                for (int i = 0; i < 8; i++) st.Cooldown[i] = Math.Max(0, Int(cd[Keys[i]]));
            if (o["lastStreak"] is JsonObject ls)
                for (int i = 0; i < 8; i++) st.Streak[i] = Math.Max(0, Int(ls[Keys[i]]));

            if (o["history"] is JsonArray ha)
            {
                foreach (var item in ha)
                {
                    if (item is not JsonObject h) continue;
                    var d = new int[8];
                    if (h["d"] is JsonObject dd)
                        for (int i = 0; i < 8; i++) d[i] = Int(dd[Keys[i]]);
                    st.History.Add((Int(h["r"]), d));
                    if (st.History.Count >= 200) break;
                }
            }

            return st;
        }
        catch
        {
            return NewDefault(npcId);   // 坏文件静默重建默认值
        }
    }

    internal static void Save(PanelState st)
    {
        if (st == null || string.IsNullOrEmpty(st.NpcId)) return;
        try
        {
            var path = MemoryPaths.PanelPath(st.NpcId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var o = new JsonObject
            {
                ["npcId"] = st.NpcId,
                ["npcName"] = st.NpcName ?? "",
                ["rounds"] = st.Rounds,
                ["nsfwIdleRounds"] = st.NsfwIdleRounds,
                ["locked"] = st.Locked,
                ["initSource"] = st.InitSource ?? "default",
                ["updatedAt"] = st.UpdatedAt ?? "",
            };

            var b = new JsonObject();
            for (int i = 0; i < 4; i++) b[Keys[4 + i]] = st.Base[i];
            o["base"] = b;

            var v = new JsonObject();
            for (int i = 0; i < 8; i++) v[Keys[i]] = st.Values[i];
            o["values"] = v;

            var cd = new JsonObject();
            var ls = new JsonObject();
            for (int i = 0; i < 8; i++)
            {
                cd[Keys[i]] = st.Cooldown[i];
                ls[Keys[i]] = st.Streak[i];
            }
            o["cooldown"] = cd;
            o["lastStreak"] = ls;

            var ha = new JsonArray();
            foreach (var (round, delta) in st.History)
            {
                var d = new JsonObject();
                for (int i = 0; i < 8; i++) if (delta[i] != 0) d[Keys[i]] = delta[i];
                ha.Add(new JsonObject { ["r"] = round, ["d"] = d });
            }
            o["history"] = ha;

            var json = o.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Warn($"[面板] 保存失败：{e.GetType().Name}: {e.Message}");
        }
    }

    // ================= p 解析（§2.2 三层：JSON → 正则 → 缺省） =================

    /// <summary>从模型原始输出里解析 "p" 数组；找不到 / 解析失败返回 null（调用方按失败降级）。</summary>
    internal static int[] ParseP(string origin)
    {
        if (string.IsNullOrEmpty(origin)) return null;

        // ① 正常 JSON
        try
        {
            if (JsonNode.Parse(origin) is JsonObject root && root["p"] is JsonArray ja)
            {
                var arr = new int[8];
                for (int i = 0; i < 8; i++) arr[i] = ToInt(i < ja.Count ? ja[i] : null);
                return arr;
            }
        }
        catch { }

        // ② 正则兜底（JSON 有瑕疵）
        var m = Regex.Match(origin, "\"p\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
        if (m.Success)
        {
            var parts = m.Groups[1].Value.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            var arr = new int[8];
            for (int i = 0; i < 8 && i < parts.Length; i++)
            {
                var s = NormalizeDigits(parts[i].Trim());
                arr[i] = int.TryParse(s, out var n) ? n : 0;
            }
            return arr;
        }

        return null;
    }

    private static int ToInt(JsonNode v)
    {
        if (v == null) return 0;
        try
        {
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out var d)) return (int)Math.Round(d);
                if (jv.TryGetValue<int>(out var i)) return i;
                if (jv.TryGetValue<string>(out var s))
                {
                    s = NormalizeDigits(s);
                    return int.TryParse(s, out var n) ? n : 0;
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>全角数字/正负号 → 半角</summary>
    private static string NormalizeDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= '０' && c <= '９') sb.Append((char)('0' + (c - '０')));
            else if (c == '－' || c == '−') sb.Append('-');
            else if (c == '＋') sb.Append('+');
            else sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>把解析出的 8 位数组渲染成"温度+1 态度0 …"（R4 对账用，也用于日志）。
    /// ★ 过审模式（hideNsfw）下只打四轴——日志可能被截图公开（2026-11-12 规矩）</summary>
    internal static string DeltaText(int[] raw)
    {
        if (raw == null) return "全 0";
        int n = PromptConfig.HideNsfw ? 4 : 8;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i == 4) sb.Append(" | ");
            sb.Append(Cn[i]).Append(raw[i] > 0 ? "+" : "").Append(raw[i]);
            if (i != n - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    // ================= delta 应用（§3.1） =================

    /// <summary>收到模型回复后调用：解析 p → 应用 → 衰减 → 落盘。与 intimacy 完全独立（R9）。</summary>
    internal static void OnModelReply(string npcId, string origin)
    {
        try
        {
            if (!PromptConfig.PanelEnabled) return;
            if (!PromptConfig.PanelInMainRequest) return;   // P0 只做主请求模式
            if (string.IsNullOrEmpty(npcId)) return;
            if (ShouldHidePanel(npcId)) return;              // ★ 男性 NPC（且未开「男角色也能用面板」）不解析 p、不写面板数据
            if (_paused) return;

            NoteActiveNpc(npcId);

            var p = ParseP(origin);
            if (p == null)
            {
                Fail(npcId, "主请求里没有 p 数组");
                return;
            }

            lock (_lock)
            {
                bool nsfwHit = false;
                try { nsfwHit = NsfwConfig.LastHit(npcId); } catch { }

                var st = Load(npcId);
                ApplyDeltas(st, p, nsfwHit);
                // ★ 2026-11-13：存本轮实际生效的 delta（面板在数值旁显示 ↑/↓/─）
                LastDelta = st.Locked ? new int[8] : (int[])p.Clone();
                LastDeltaNpc = npcId;
                ApplyDecay(st);
                st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
                Save(st);   // rounds 每轮都变，直接落盘（约 1KB，开销可忽略）
                ResetFail(npcId);

                Log.LogInfo($"[面板] p=[{string.Join(",", p)}] → {DeltaText(p)} | 当前 {ValuesText(st)}");
            }
        }
        catch (Exception e)
        {
            Warn($"[面板] p 解析/应用异常：{e.GetType().Name}: {e.Message}");
        }
    }

    private static void ApplyDeltas(PanelState st, int[] raw, bool nsfwHit)
    {
        if (st.Locked) return;   // 锁定 = 完全冻结

        // ① NSFW 双保险：非亲密场景强制清零（不启用 NSFW 四项也一样）
        //    ★ 2026-11-12 过审模式（hideNsfw）走同一条清零路径（施工图 §4.7）
        if (!nsfwHit || !PromptConfig.PanelNsfw || PromptConfig.HideNsfw)
            for (int i = 4; i < 8; i++) raw[i] = 0;

        // ② 判定期 clamp
        int fourLim = Math.Clamp(Math.Max(PromptConfig.PanelStepMax, PromptConfig.PanelStrongStep), 1, 2);
        int nsfwLim = Math.Clamp(PromptConfig.PanelNsfwStepMax, 1, 2);
        for (int i = 0; i < 4; i++) raw[i] = Math.Clamp(raw[i], -fourLim, fourLim);
        for (int i = 4; i < 7; i++) raw[i] = Math.Clamp(raw[i], -nsfwLim, nsfwLim);
        raw[7] = Math.Clamp(raw[7], 0, 1);   // 崩坏只能 0 / +1

        // ③ 冷却检查：同一轴连续 N 轮变化 → 停 M 轮
        int streakTrigger = Math.Max(2, PromptConfig.PanelCooldownStreak);
        int latch = Math.Max(0, PromptConfig.PanelCooldownLatch);
        for (int i = 0; i < 8; i++)
        {
            if (st.Cooldown[i] > 0)
            {
                raw[i] = 0;
                st.Cooldown[i]--;
                continue;
            }
            if (raw[i] != 0)
            {
                if (++st.Streak[i] >= streakTrigger)
                {
                    st.Streak[i] = 0;
                    st.Cooldown[i] = latch;
                }
            }
            else st.Streak[i] = 0;
        }

        // ④ 叠加 + 边界 clamp
        for (int i = 0; i < 4; i++) st.Values[i] = Math.Clamp(st.Values[i] + raw[i], -100, 100);
        for (int i = 4; i < 8; i++) st.Values[i] = Math.Clamp(st.Values[i] + raw[i], 0, 100);

        // ⑤ 更新衰减计时（铁规一：按空闲轮数）
        st.NsfwIdleRounds = nsfwHit ? 0 : st.NsfwIdleRounds + 1;

        // ⑥ history（只记非零轮次，留最近 200 轮）
        bool any = false;
        for (int i = 0; i < 8; i++) if (raw[i] != 0) { any = true; break; }
        if (any)
        {
            st.History.Add((st.Rounds, (int[])raw.Clone()));
            if (st.History.Count > 200) st.History.RemoveAt(0);
        }
    }

    /// <summary>衰减：每轮调一次（与 delta 判定解耦）。§3.2 / §3.2.1</summary>
    private static void ApplyDecay(PanelState st)
    {
        if (st.Locked) return;

        st.Rounds++;

        // ① 四轴：按「总轮数」，每 PanelDecayEvery 轮向 0 回 1
        if (PromptConfig.PanelDecayEvery > 0 && st.Rounds % PromptConfig.PanelDecayEvery == 0)
            for (int i = 0; i < 4; i++)
                st.Values[i] = StepToward(st.Values[i], 0, 1);

        // ② NSFW 四项：按「空闲轮数」，朝该角色自己的 base 回 1
        if (!PromptConfig.PanelNsfw || !PromptConfig.PanelNsfwDecayIdle) return;
        int idle = st.NsfwIdleRounds;
        if (idle <= 0) return;

        if (idle % 15 == 0)
        {
            st.Values[4] = StepToward(st.Values[4], st.Base[0], 1);
            st.Values[5] = StepToward(st.Values[5], st.Base[1], 1);
        }
        if (idle % 20 == 0)
            st.Values[6] = StepToward(st.Values[6], st.Base[2], 1);
        if (idle % 30 == 0)
            st.Values[7] = Math.Max(0, st.Values[7] - 1);
    }

    private static int StepToward(int v, int target, int step)
    {
        if (v > target) return Math.Max(target, v - step);
        if (v < target) return Math.Min(target, v + step);
        return v;
    }

    // ================= 供注入侧读取 =================

    /// <summary>注入前读当前状态（不修改）。</summary>
    internal static PanelState Peek(string npcId) => Load(npcId);

    /// <summary>调试 / 作弊：直接设某个轴（axis 0~7）；返回是否成功。</summary>
    internal static bool SetValue(string npcId, int axis, int value)
    {
        if (string.IsNullOrEmpty(npcId) || axis < 0 || axis > 7) return false;
        lock (_lock)
        {
            var st = Load(npcId);
            st.Values[axis] = axis < 4 ? Math.Clamp(value, -100, 100) : Math.Clamp(value, 0, 100);
            st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Save(st);
            Log.LogInfo($"[面板] 手动设值 {Cn[axis]}={st.Values[axis]}（{npcId}）");
            return true;
        }
    }

    /// <summary>锁定 / 解锁（§3.3：锁定 = delta 与衰减全停）</summary>
    internal static void SetLocked(string npcId, bool locked)
    {
        if (string.IsNullOrEmpty(npcId)) return;
        lock (_lock)
        {
            var st = Load(npcId);
            st.Locked = locked;
            st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Save(st);
            Log.LogInfo($"[面板] {(locked ? "已锁定" : "已解锁")}（{npcId}）");
        }
    }

    /// <summary>模板套用（16 原型 → 只改四轴，不碰 NSFW 的 base）</summary>
    internal static bool ApplyArchetype(string npcId, string name)
    {
        if (string.IsNullOrEmpty(npcId)) return false;
        lock (_lock)
        {
            var st = Load(npcId);
            if (!PanelArchetype.TryApply(st, name)) return false;
            st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Save(st);
            Log.LogInfo($"[面板] 套用模板「{name}」→ 温度{st.Values[0]} 态度{st.Values[1]} 主动{st.Values[2]} 话量{st.Values[3]}");
            return true;
        }
    }

    /// <summary>[重置本角色]：本地关键词重算 base + values 归位（保留轮数/锁定，§5.1 零成本）</summary>
    internal static void ResetByKeywords(string npcId)
    {
        if (string.IsNullOrEmpty(npcId)) return;
        lock (_lock)
        {
            var cur = Load(npcId);
            var fresh = PanelInit.InitByKeywords(npcId);
            fresh.Rounds = cur.Rounds;
            fresh.NsfwIdleRounds = cur.NsfwIdleRounds;
            fresh.Locked = cur.Locked;
            fresh.NpcName = cur.NpcName;
            fresh.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Save(fresh);
            Log.LogInfo($"[面板] 已按本地规则重置（{npcId}，来源 {fresh.InitSource}）");
        }
    }

    /// <summary>
    /// [按角色卡重新推断]：调 1 次 API（约 500 token，点了才花）；失败回退本地关键词规则（§5.3）。
    /// </summary>
    internal static async System.Threading.Tasks.Task<(bool Ok, string Msg)> ReInitByLlmAsync(string npcId)
    {
        if (string.IsNullOrEmpty(npcId)) return (false, "没有角色 ID");
        try
        {
            var cfg = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
            if (cfg == null || !cfg.IsValid)
            {
                Log.LogWarning("[面板初始化] 拿不到 API 配置 → 回退本地规则");
                ResetByKeywords(npcId);
                return (false, "拿不到 API 配置，已回退本地规则");
            }

            var (ok, st, msg) = await PanelInit.AskLlmAsync(npcId, cfg);
            if (!ok || st == null)
            {
                ResetByKeywords(npcId);
                return (false, (msg ?? "AI 推断失败") + "，已回退本地规则");
            }

            lock (_lock)
            {
                var cur = Load(npcId);
                st.Rounds = cur.Rounds;
                st.NsfwIdleRounds = cur.NsfwIdleRounds;
                st.Locked = cur.Locked;
                st.NpcName = cur.NpcName;
                st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
                Save(st);
            }
            return (true, msg ?? "AI 推断成功");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[面板初始化] AI 推断异常：{e.GetType().Name}: {e.Message} → 回退本地");
            try { ResetByKeywords(npcId); } catch { }
            return (false, "AI 推断失败，已回退本地规则");
        }
    }

    /// <summary>HUD 拖动滑块后直接保存（不做判定）</summary>
    internal static void SaveDirect(PanelState st)
    {
        if (st == null || string.IsNullOrEmpty(st.NpcId)) return;
        lock (_lock)
        {
            st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Save(st);
        }
    }

    /// <summary>日志用：当前 8 个值。
    /// ★ 过审模式（hideNsfw）下只打四轴——日志可能被截图公开（2026-11-12 规矩）</summary>
    internal static string ValuesText(PanelState st)
    {
        if (st == null) return "";
        int n = PromptConfig.HideNsfw ? 4 : 8;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i == 4) sb.Append(" | ");
            sb.Append(Cn[i]).Append('=').Append(st.Values[i]);
            if (i != n - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    internal static string Describe(string npcId)
    {
        var st = Load(npcId);
        return $"面板 rounds={st.Rounds} idle={st.NsfwIdleRounds} locked={st.Locked} src={st.InitSource} | {ValuesText(st)}";
    }

    // ================= 失败降级（§2.7） =================

    private static void Fail(string npcId, string why)
    {
        lock (_lock)
        {
            _fail.TryGetValue(npcId, out var n);
            n++;
            _fail[npcId] = n;

            if (n >= 3 && !_paused)
            {
                _paused = true;
                Log.LogError($"[面板] 连续 {n} 轮解析失败 → 本局暂停面板（重启游戏恢复）");
            }
            else if ((DateTime.Now - _lastWarn).TotalSeconds >= 60)
            {
                _lastWarn = DateTime.Now;
                Log.LogWarning($"[面板] {why}（连续 {n} 轮）");
            }
        }
    }

    private static void ResetFail(string npcId)
    {
        _fail[npcId] = 0;
        if (_paused) { _paused = false; Log.LogInfo("[面板] 解析恢复，面板解除暂停"); }
    }

    private static void Warn(string msg)
    {
        if ((DateTime.Now - _lastWarn).TotalSeconds < 60) return;
        _lastWarn = DateTime.Now;
        Log.LogWarning(msg);
    }

    // ================= JSON 小工具 =================

    private static string Str(JsonNode v)
    {
        try { return v?.GetValue<string>(); } catch { return null; }
    }

    private static int Int(JsonNode v, int dflt = 0)
    {
        if (v == null) return dflt;
        try { return (int)Math.Round(v.GetValue<double>()); } catch { }
        try { return int.Parse(v.ToString()); } catch { }
        return dflt;
    }

    private static bool Bool(JsonNode v)
    {
        try { return v != null && v.GetValue<bool>(); } catch { return false; }
    }
}
