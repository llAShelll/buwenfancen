using System;
using HarmonyLib;

namespace BuWenFanChen;

/// <summary>
/// ★ 2026-11-05：历史预算放宽（原为只读探针，现改为「改预算」）
///
/// 背景（2026-11-05 03:05 实测）：
///   CompleteBudgeted 收到的 history 被我们换成 canon（65 条），
///   但游戏内部 ChatBudget 又按自己的预算砍回 21 条 —— 白换。
///   实测参数：
///     FitChars : maxChars=24000, memoryMaxChars=2000, recentRoundReserve=4
///                fixedPrefix=4089, dynamicTail=8208
///                → history 只剩 24000-4089-8208 ≈ 11,700 字符 ≈ 21 条
///     FitTokens: contextSize=64000, maxTokens=16000, charsPerToken=1.5
///   而 settings.json 的 contextSize 是 64000 —— 游戏只用了不到 1/4。
///
/// 本补丁只放大这两个上限，不动任何内容：
///   maxChars   24000 → 60000   (60000/1.5 ≈ 40000 token，仍在 64000 内)
///   maxTokens  16000 → 40000
///   memoryMaxChars 2000 → 8000（记忆块也放宽，免得被挤）
/// </summary>
internal static class ChatBudgetProbe
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·预算探针");

    private static int _charsLog;
    private static int _tokensLog;
    private static bool _planDumped;
    private static int _planCount;
    private const int MaxLog = 6;

    /// <summary>FitChars 的新上限（字符）</summary>
    internal static int NewMaxChars = 60000;

    /// <summary>FitChars 的记忆上限（字符）</summary>
    internal static int NewMemoryMaxChars = 8000;

    /// <summary>FitTokens 的新上限（token）</summary>
    internal static int NewMaxTokens = 40000;

    /// <summary>★★★ 2026-11-10 铁律（主人破案，玩家日志实证）：
    ///   `FitTokens` 的 `maxTokens` 参数 = 游戏 `ainpc_custom.json` 里的 **`max_tokens`**，
    ///   也就是**最终发给模型 API 的 max_tokens**（玩家可在「高级设置」里自己调，默认 16000 / 有的环境 4000）。
    ///
    ///   旧版把它放大到 40000 / 21092 → 超过 API 的 max_tokens 上限 → HTTP 400
    ///   → 游戏把 400 显示成「上下文超出模型上限，请在设置里调小上下文长度」。
    ///   ← 玩家报这个错就是这么来的（他的 max_tokens 是 4000，被我们撑到 21092 必爆）。
    ///
    ///   **所以：这个值绝对不能改**。本类从此只做只读探针（FitChars 的 maxChars 也不再放大，
    ///   避免请求变大后在某些模型上超上下文）。想给玩家更多历史，让玩家自己调：
    ///   ① 游戏设置里的「上下文长度」② ainpc_custom.json 的 max_tokens ③ 上下文档位。
    /// </summary>
    /// <summary>
    /// 上一次 `FitTokens` 看到的 `maxTokens`（= 玩家 ainpc_custom.json 里的 `max_tokens`）。
    /// **只用来估算 history 还能占多少字符，绝不写回去。**
    /// </summary>
    private static int _lastMaxTokens = -1;

    private static int _lastFitChars;
    private static int _lastContextSize;
    private static float _lastCharsPerToken = 1.5f;

    /// <summary>maxChars 放大的上限（字符）。1.3 时期就是这个值，11-07 实跑 576 次命中 68.2~73.3%</summary>
    private const int MaxCharsCeil = 60000;

    /// <summary>按真实上下文算预算时留的余量</summary>
    private const float BudgetSafety = 0.85f;

    /// <summary>给请求大小探针用：取上一次拿到的上下文/输出上限/换算系数。</summary>
    internal static void Snapshot(out int contextSize, out int maxTokens, out float charsPerToken)
    {
        contextSize = _lastContextSize;
        maxTokens = _lastMaxTokens;
        charsPerToken = _lastCharsPerToken > 0.1f ? _lastCharsPerToken : 1.5f;
    }

    // ============ ★ 2026-11-11：上下文参数跨启动缓存 ============
    //
    // 问题：`FitChars` 在 `FitTokens` 之前执行，**首轮拿不到 contextSize/maxTokens**
    //   → 1.3.3 的 maxChars 动态放大在首轮不生效 → 用游戏原值 24,000
    //   → 玩家如果 tail 很肥（实测有个玩家 NSFW 块 10,169 字、tail 18,989），
    //     首轮 fixed+tail 就吃满 24,000，history 预算只剩 287 字
    //   → 游戏把历史裁到 1~2 条 → canon 对不齐 → ★重建（一轮白费 + 触发后台合并）
    //
    // 做法：把上次拿到的 contextSize / maxTokens / charsPerToken 落到插件 config 目录，
    //   下次启动首轮就能直接拿来算，不用干等一轮。
    //   风险控制：只在值变化时写盘；读失败/文件坏了都静默回退（等于没这功能）。
    private static string BudgetCachePath =>
        System.IO.Path.Combine(PromptConfig.Dir, "budget_cache.json");

    private static bool _cacheLoaded;
    private static int _savedCtx = -1, _savedMt = -1;
    private static float _savedCpt = -1f;

    private static void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;

        // ★ 2026-11-12 改法：**游戏配置优先、缓存补 charsPerToken**。
        //
        //   旧版只读 budget_cache.json，结果是「第一次装这个版本的人」首轮必然拿不到
        //   （缓存要等第一轮 FitTokens 跑完才写盘，而 FitChars 在它之前跑）——
        //   玩家 6 号日志实证：`ctx=0 maxTok=-1` → maxChars 仍是 24000 → history 预算只剩 2,923 字。
        //
        //   现在：settings.json / ainpc_custom.json 里本来就有这两个值，每次启动现读，
        //   玩家改设置立刻生效、不会读到过期值；缓存只负责提供游戏内部的 charsPerToken。
        int ctxCfg = 0, mtCfg = 0;
        try { ReadGameContextSettings(out ctxCfg, out mtCfg); } catch { }
        if (ctxCfg > 0) _lastContextSize = ctxCfg;
        if (mtCfg > 0) _lastMaxTokens = mtCfg;

        // 补 charsPerToken（配置里没有，只有运行时才知道；没有就保持默认 1.5）
        float cptCached = -1f;
        try
        {
            var p = BudgetCachePath;
            if (System.IO.File.Exists(p))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    System.IO.File.ReadAllText(p, System.Text.Encoding.UTF8));
                if (node is System.Text.Json.Nodes.JsonObject o)
                {
                    try { cptCached = o["charsPerToken"]?.GetValue<float>() ?? -1f; } catch { }
                }
            }
        }
        catch { }
        if (cptCached > 0.1f) _lastCharsPerToken = cptCached;

        if (_lastContextSize > 0 && _lastMaxTokens > 0)
        {
            Log.LogInfo($"[预算] 启动兜底取到上下文参数：ctx={_lastContextSize} maxTok={_lastMaxTokens} cpt={_lastCharsPerToken:F2}"
                        + (cptCached > 0.1f ? "（cpt 来自缓存）" : "（cpt 用默认 1.5）")
                        + " → 首轮即可按真实上下文放大 maxChars");
        }
        else
        {
            Log.LogInfo($"[预算] 启动读不到上下文参数（ctx={ctxCfg} maxTok={mtCfg}）→ 首轮按游戏原值 24000 裁剪");
        }
    }

    /// <summary>游戏常量默认值：`TbConstants.AINPC_CUSTOM_MAX_TOKENS`</summary>
    private const int GameDefaultMaxTokens = 4000;

    /// <summary>
    /// ★ 2026-11-12：从游戏自己的配置读上下文参数（给首轮兜底用）。
    ///   · contextSize → settings.json 的 ainpc.contextSize（= 游戏「上下文长度」设置）
    ///   · maxTokens   → ainpc_custom.json 的 max_tokens（读不到用游戏常量 4000）
    /// 两个文件都在 persistentDataPath（LocalLow\Nuverse\WorldApart）下，和 MemoryPaths.Root 同级。
    /// 读不到就返回 0，调用方自己兜底。**只用于本地裁剪预算，绝不写回 API 的 max_tokens**。
    /// </summary>
    private static void ReadGameContextSettings(out int ctx, out int maxTokens)
    {
        ctx = 0; maxTokens = 0;

        string dir = null;
        try
        {
            var root = BuWenFanChen.Memory.MemoryPaths.Root;   // ...\WorldApart\BuWenFanChen
            dir = System.IO.Path.GetDirectoryName(root);
        }
        catch { }
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            var p = System.IO.Path.Combine(dir, "settings.json");
            if (System.IO.File.Exists(p))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    System.IO.File.ReadAllText(p, System.Text.Encoding.UTF8));
                if (node is System.Text.Json.Nodes.JsonObject o
                    && o["ainpc"] is System.Text.Json.Nodes.JsonObject a)
                {
                    try { ctx = a["contextSize"]?.GetValue<int>() ?? 0; } catch { }
                    try { maxTokens = a["max_tokens"]?.GetValue<int>() ?? 0; } catch { }
                }
            }
        }
        catch { }

        if (maxTokens <= 0)
        {
            try
            {
                var p = System.IO.Path.Combine(dir, "ainpc_custom.json");
                if (System.IO.File.Exists(p))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(
                        System.IO.File.ReadAllText(p, System.Text.Encoding.UTF8));
                    if (node is System.Text.Json.Nodes.JsonObject o && o["max_tokens"] != null)
                        maxTokens = o["max_tokens"].GetValue<int>();
                }
            }
            catch { }
        }

        if (maxTokens <= 0) maxTokens = GameDefaultMaxTokens;
    }

    private static void SaveCacheIfChanged()
    {
        try
        {
            if (_lastContextSize <= 0 || _lastMaxTokens <= 0) return;
            if (_savedCtx == _lastContextSize && _savedMt == _lastMaxTokens
                && Math.Abs(_savedCpt - _lastCharsPerToken) < 0.01f) return;

            _savedCtx = _lastContextSize;
            _savedMt = _lastMaxTokens;
            _savedCpt = _lastCharsPerToken;

            var cpt = _lastCharsPerToken.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            var json = $"{{\"contextSize\":{_lastContextSize},\"maxTokens\":{_lastMaxTokens},"
                     + $"\"charsPerToken\":{cpt},\"updatedAt\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"}}";
            System.IO.File.WriteAllText(BudgetCachePath, json, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    internal static void Reset() { _charsLog = 0; _tokensLog = 0; }

    [HarmonyPrefix]
    [HarmonyPatch("JNGame.Ainpc.Chat.ChatBudget", "FitChars")]
    internal static void FitCharsPrefix(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length <= 8) return;

            // ★ 首轮也要能放大：先尝试载入上次的上下文参数
            EnsureCacheLoaded();

            int oldMax = ArgInt(__args, 6);
            int oldMem = ArgInt(__args, 7);
            int fixedLen = ArgLen(__args, 0);
            int tailLen = ArgLen(__args, 3);
            int histCount = ArgCount(__args, 2);
            _lastFitChars = oldMax;

            // ★★★ 2026-11-11：maxChars 恢复「按玩家真实上下文动态放大」。
            //
            //  完整因果（今晚查实，有日志/抓包/实测三重证据）：
            //    · 11-05 把 maxChars 写死放大到 60000 → history 预算 49,207 字
            //      → canon 20 轮根本不会被裁 → history 真正 append-only
            //      → 11-07 实跑 576 次请求，命中 68.2~73.3%
            //    · 11-10 22:37 为了修「上下文超限」，把 maxChars 和 maxTokens **一起**关成只读
            //      → 玩家侧 fixed 11,195 + tail 11,574 = 22,769，几乎吃满 24,000
            //      → history 预算只剩 1,231 字 → 每轮只能留 2~3 条、每轮从头裁
            //      → 缓存断点回到 messages[2] → 实测命中跌到 15.6%，连带记忆也变差
            //
            //  关键区分（当时混为一谈了）：
            //    · FitTokens.maxTokens = 发给 API 的 max_tokens → **绝对只读**（超了必 400）
            //    · FitChars.maxChars   = 游戏本地裁 history 的预算 → **可以放大**，不影响 400
            //
            //  所以这里按 (contextSize - maxTokens) 算真实可用输入，双向 clamp：
            //    小上下文玩家 → 算出来比 24000 还小 → 自动缩 → 不会 400
            //    大上下文玩家 → 算到 60000 → history 有空间 → 命中回到 68%
            int newMax = oldMax;
            try
            {
                int ctx = _lastContextSize;
                int mt = _lastMaxTokens;
                float cpt = _lastCharsPerToken > 0.1f ? _lastCharsPerToken : 1.5f;
                if (ctx > 0 && mt > 0 && oldMax > 0)
                {
                    long usable = (long)((ctx - mt) * (double)cpt * BudgetSafety);
                    if (usable > MaxCharsCeil) usable = MaxCharsCeil;
                    // 允许比游戏原值更小：游戏的 24000 是写死常量，不看 player 的 contextSize，
                    // 小上下文玩家（16K/32K）按它裁照样会撞 400 —— 这时要主动缩。
                    if (usable < 4000) usable = 4000;   // 保底，别裁到一条历史都不剩
                    newMax = (int)usable;
                }
            }
            catch { }

            if (newMax != oldMax && __args.Length > 6)
            {
                __args[6] = newMax;
                int readback = ArgInt(__args, 6);
                Log.LogInfo($"[预算] ★ maxChars {oldMax} → {newMax}（回读={readback}）"
                            + $" | ctx={_lastContextSize} maxTok={_lastMaxTokens} cpt={_lastCharsPerToken:F2}"
                            + " —— 只改本地裁剪预算，不动发给 API 的 max_tokens");
            }

            _charsLog++;
            bool logNow = _charsLog <= MaxLog || _charsLog % 20 == 0;
            if (logNow)
            {
                int historyBudget = newMax > 0 ? newMax - Math.Max(0, fixedLen) - Math.Max(0, tailLen) : -1;
                Log.LogInfo($"[预算] FitChars 第{_charsLog}次：maxChars={newMax}(游戏原{oldMax}) memMax={oldMem}"
                            + $" hist={histCount}条 fixed={fixedLen} tail={tailLen}"
                            + $" → history 预算约 {historyBudget} 字"
                            + $" | ctx={_lastContextSize} maxTok={_lastMaxTokens}");
            }
        }
        catch (Exception e)
        {
            try { Log.LogWarning("[预算] FitChars 探针失败：" + e.GetType().Name + " " + e.Message); } catch { }
        }
    }

    /// <summary>
    /// ★ FitChars 返回的是 Plan（Il2Cpp 包装类），历史在字段 PHistory 里。
    ///   先探测字段取值是否可读，再直接写回未裁剪的 history。
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("JNGame.Ainpc.Chat.ChatBudget", "FitChars")]
    internal static void FitCharsPostfix(ref object __result, object[] __args)
    {
        try
        {
            if (__result == null || __args == null || __args.Length < 3) return;
            var orig = __args[2];
            if (orig == null) return;
            int nOrig = ArgCount(__args, 2);

            var t = __result.GetType();
            var flags = System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance;

            if (!_planDumped)
            {
                _planDumped = true;
                // 逐个字段探测「能不能读」
                var sb = new System.Text.StringBuilder("[预算] Plan 字段探测:");
                foreach (var f in t.GetFields(flags))
                {
                    string got;
                    try
                    {
                        var v = f.GetValue(__result);
                        if (v == null) got = "null";
                        else
                        {
                            int n = BuWenFanChen.History.CanonStore.CountItems(v);
                            got = v.GetType().Name + (n >= 0 ? "(" + n + "条)" : "");
                        }
                    }
                    catch (Exception e) { got = "读异常:" + e.GetType().Name; }
                    sb.Append(' ').Append(f.Name).Append('=').Append(got);
                }
                Log.LogInfo(sb.ToString());

                var props = new System.Text.StringBuilder("[预算] Plan 属性探测:");
                foreach (var p in t.GetProperties(flags))
                {
                    if (!p.CanRead) { props.Append(' ').Append(p.Name).Append("=不可读"); continue; }
                    string got;
                    try
                    {
                        var v = p.GetValue(__result);
                        got = v == null ? "null" : v.GetType().Name;
                    }
                    catch (Exception e) { got = "读异常:" + e.GetType().Name; }
                    props.Append(' ').Append(p.Name).Append('=').Append(got);
                }
                Log.LogInfo(props.ToString());
            }

            // ── 直接写 PHistory ──
            var histField = t.GetField("PHistory", flags) ?? t.GetField("History", flags);
            var histProp = t.GetProperty("History", flags);

            bool ok = false;
            if (histField != null)
            {
                try
                {
                    histField.SetValue(__result, orig);
                    ok = true;
                    Log.LogInfo($"[预算] ★ 已写回 Plan.{histField.Name} = {nOrig} 条（未裁剪）");
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[预算] 写 Plan.{histField.Name} 失败：{e.GetType().Name} {e.Message}");
                }
            }
            if (!ok && histProp != null && histProp.CanWrite)
            {
                try
                {
                    // ★ Plan.History 的类型是 List<T>，必须先转成对应泛型列表
                    var typed = BuWenFanChen.History.CanonStore.ToTypedList(orig);
                    if (typed == null)
                    {
                        Log.LogWarning("[预算] 转 List<T> 失败，跳过属性写入");
                    }
                    else
                    {
                        histProp.SetValue(__result, typed);
                        ok = true;
                        Log.LogInfo($"[预算] ★ 已写回 Plan.History 属性 = {nOrig} 条（未裁剪，已转 List<T>）");
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[预算] 写 Plan.History 属性失败：{e.GetType().Name} {e.Message}");
                }
            }
            if (!ok) Log.LogWarning("[预算] Plan 的 history 字段/属性都写不进去");

            // ★ 2026-11-12：把 Plan 自己的「能不能发」判定打出来。
            //
            //   为什么加：玩家装 1.3.3 后仍有「空响应」，日志里
            //     LlmResult.Error = ContextBudgetExceeded
            //     RawError      = "generated content exceeds budget"
            //     HttpCode = 0 / PromptTokens = 0 / CompletionTokens = 0
            //   → 这是**游戏自己**在发请求前判定超预算，请求根本没出去。
            //   Plan 上有 CanSend / EstimatedTokens / TotalChars / TrimReason 这几个属性，
            //   游戏就是拿它们判的 —— 打出来才知道到底哪个数超了。
            _planCount++;
            string canSend = ReadPlanStr(__result, "CanSend");
            int estTok = ReadPlanInt(__result, "EstimatedTokens");
            int totalChars = ReadPlanInt(__result, "TotalChars");
            int fixedChars = ReadPlanInt(__result, "FixedChars");
            int dropped = ReadPlanInt(__result, "DroppedHistoryCount");
            string trim = ReadPlanStr(__result, "TrimReason");

            var planLine = $"[预算] Plan 第{_planCount}次：CanSend={canSend} EstimatedTokens={estTok}"
                         + $" TotalChars={totalChars} FixedChars={fixedChars} 丢历史={dropped}"
                         + $" TrimReason={trim}";
            if (canSend == "False")
                Log.LogWarning(planLine + "   ★★ 游戏会拒绝发送 → 玩家看到 ContextBudgetExceeded");
            else if (_planCount <= 10 || _planCount % 20 == 0)
                Log.LogInfo(planLine);
        }
        catch (Exception e)
        {
            try { Log.LogWarning("[预算] FitChars Postfix 失败：" + e.GetType().Name + " " + e.Message); } catch { }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch("JNGame.Ainpc.Chat.ChatBudget", "FitTokens")]
    internal static void FitTokensPrefix(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length <= 7) return;

            int oldMax = ArgInt(__args, 5);
            int ctx = ArgInt(__args, 4);
            float cpt = ArgFloat(__args, 6);

            if (ctx > 0) _lastContextSize = ctx;
            if (cpt > 0.1f) _lastCharsPerToken = cpt;
            // ★ 2026-11-11：缓存 max_tokens（只用于算 history 字符预算，不写回）
            if (oldMax > 0) _lastMaxTokens = oldMax;
            // ★ 顺手把上下文参数落到文件，下次启动首轮就能用（见 BudgetCachePath 注释）
            SaveCacheIfChanged();

            // ★★★ 2026-11-10 铁律：**绝对不改 maxTokens**。
            //   它就是 ainpc_custom.json 的 max_tokens —— 发给模型 API 的输出上限。
            //   改成超过 API 上限 → HTTP 400 → 游戏显示「上下文超出模型上限」。
            //   （2026-11-11 补：FitChars 的 maxChars 不在此限，见 FitCharsPrefix 的注释）
            _tokensLog++;
            if (_tokensLog <= MaxLog || _tokensLog % 20 == 0)
            {
                Log.LogInfo($"[预算] FitTokens（只读）：maxTokens={oldMax}"
                            + $" contextSize={ctx} charsPerToken={cpt:F3} recentRoundReserve={ArgInt(__args, 7)}");
            }
        }
        catch (Exception e)
        {
            try { Log.LogWarning("[预算] FitTokens 探针失败：" + e.GetType().Name + " " + e.Message); } catch { }
        }
    }

    // ───────── Plan 属性读取（读不到给 "?" / -1，判不出时不要误报警）─────────

    private static string ReadPlanStr(object plan, string name)
    {
        try
        {
            var p = plan?.GetType().GetProperty(name);
            if (p == null) return "?";
            var v = p.GetValue(plan);
            if (v == null) return "null";
            return v.ToString();
        }
        catch { return "?<" + name + ">"; }
    }

    private static int ReadPlanInt(object plan, string name)
    {
        try
        {
            var p = plan?.GetType().GetProperty(name);
            if (p == null) return -1;
            var v = p.GetValue(plan);
            return v == null ? -1 : Convert.ToInt32(v);
        }
        catch { return -1; }
    }

    // ───────── 取值工具 ─────────

    private static int ArgInt(object[] a, int i)
    {
        try
        {
            if (i >= a.Length || a[i] == null) return -999;
            return Convert.ToInt32(a[i]);
        }
        catch { return -999; }
    }

    private static float ArgFloat(object[] a, int i)
    {
        try
        {
            if (i >= a.Length || a[i] == null) return -999f;
            return Convert.ToSingle(a[i]);
        }
        catch { return -999f; }
    }

    private static int ArgLen(object[] a, int i)
    {
        try
        {
            if (i >= a.Length) return -1;
            var v = a[i];
            if (v == null) return -1;
            if (v is string s) return s.Length;
            return -2;
        }
        catch { return -2; }
    }

    /// <summary>集合类参数用：拿它有多少个元素（history 是 IReadOnlyList 不是 int）</summary>
    private static int ArgCount(object[] a, int i)
    {
        try
        {
            if (i >= a.Length || a[i] == null) return -1;
            return BuWenFanChen.History.CanonStore.CountItems(a[i]);
        }
        catch { return -2; }
    }
}
