using System.Text;
using HarmonyLib;
using A1State = BuWenFanChen.Output.ResponseNormalizer.A1TtsV2State;
using BuWenFanChen.Output;
using BuWenFanChen.Memory;

namespace BuWenFanChen;

/// <summary>
/// 输出侧处理：模型回复回来、游戏展示前。
/// · messageText = 展示给玩家的文本
/// · npcRawOutput = 模型原始输出（含 <a1tts_v1> 帧）
/// 职责：字数切分（句界，不破坏括号/引号/帧）+ 观察 dump。
/// 与语音 MOD 共存：我们只动 messageText，帧交给语音 MOD 从 raw 里处理。
/// </summary>
[HarmonyPatch(typeof(Game.Model.NpcModel), "AddNpcChatMessage")]
// ★★ 2026-10-24 17:40（对齐 A1TTSV2-CONSUMER-RULES 第 7 节）：
//   语音 MOD 的「正文一致性」检查是拿【被消费的 raw.content】和【实际显示文本】比，
//   两边都先去完整括号、空白折成一个空格，再逐字比较；一变就 invalid。
//
//   所以我们必须【在语音 MOD 之前】把 messageText 和 raw.content 一起改完，
//   让它读到的就已经是同一份文本；否则它先记录原始 244 字、我们改成 186 字，
//   它的 Postfix 一比就炸 → display_spoken_content_changed → 整轮不朗读。
//
//   Harmony 规则：Prefix 按 Priority 从高到低执行 → 设 High 保证我们第一个跑。
[HarmonyPriority(Priority.High)]
internal static class AddNpcChatMessagePatch
{
    private static readonly object Gate = new();

    /// <summary>本次 AddNpcChatMessage 调用是不是开场白（Prefix 设，Postfix 读）</summary>
    [ThreadStatic] private static bool _openingInThisCall;
    private static string _dir;
    // 三层字数上限（2026-10-23 03:32 主人定：总 200 / 旁白 120 / 台词 80）
    // ★ 硬上限 ≠ 每轮必须写满：写不到也不扣，超了才切
    // ★ 总上限 = 旁白 + 台词（保持自洽，避免两套上限互相打架）
    // ★ 台词 80 是为了 TTS 友好：实测约 8.7 字/秒，80 字 ≈ 9 秒音频，合成不拖沓
    // ★ 强制裁剪阈值 = 上限 × HardRatio（2.0 → 台词 160 / 旁白 240）；总硬截另见 ×1.5 那处
    // ★ 2026-10-24 19:05：改成从 config.json 读（玩家可改），不再硬编码。
    //   每次裁段时现取 PromptConfig 的值，所以改配置后重启即生效。
    private static int _maxContentChars => PromptConfig.ReplyLenTotal;
    private static int _maxNarrationChars => PromptConfig.ReplyLenNarration;
    private static int _maxDialogueChars => PromptConfig.ReplyLenDialogue;

    // 降级检测统计
    private static int _windowTotal;
    private static int _windowFail;
    private const int WINDOW = 20;                 // 统计窗口
    private const double FAIL_THRESHOLD = 0.3;     // 失败率阈值

    // ★ TTS 是否存在（2026-10-23 21:35 Havoc 要求：没装 TTS 时要正确回归，不要补写垃圾字段）
    private static bool? _ttsPresent;

    /// <summary>
    /// 检测语音 MOD（A1IndexTTSMod）是否加载。结果缓存，只查一次。
    /// 没装就完全不碰 a1ttsv2：不补写、不校验、不重建，只同步 content。
    /// 检测顺序：已加载程序集 → 插件目录里的 DLL 文件。
    /// </summary>
    internal static bool TtsPresent()
    {
        if (_ttsPresent.HasValue) return _ttsPresent.Value;
        try
        {
            // ① 已加载程序集里找（BepInEx 加载插件时就会加载它的 assembly）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name ?? "";
                if (n.IndexOf("A1IndexTTS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("IndexTTS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _ttsPresent = true;
                    return true;
                }
            }

            // ② 兜底：扫 plugins 目录下的 DLL 文件名
            try
            {
                var selfDir = System.IO.Path.GetDirectoryName(typeof(AddNpcChatMessagePatch).Assembly.Location);
                var pluginsDir = selfDir != null
                    ? System.IO.Path.GetDirectoryName(selfDir)
                    : null;
                if (!string.IsNullOrEmpty(pluginsDir) && System.IO.Directory.Exists(pluginsDir))
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(pluginsDir, "*.dll",
                                 System.IO.SearchOption.AllDirectories))
                    {
                        var fn = System.IO.Path.GetFileName(f) ?? "";
                        if (fn.IndexOf("A1IndexTTS", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _ttsPresent = true;
                            return true;
                        }
                    }
                }
            }
            catch { }

            _ttsPresent = false;
        }
        catch
        {
            // 检测失败时保守地认为「有」——宁可多补写，也不要让装了 TTS 的玩家失去兜底
            _ttsPresent = true;
        }
        try
        {
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(
                _ttsPresent.Value
                    ? "[输出] 语音 MOD 检测：已加载 → a1ttsv2 补写/校验生效"
                    : "[输出] 语音 MOD 检测：未安装 → 只同步 content，不补写 a1ttsv2");
        }
        catch { }
        return _ttsPresent.Value;
    }

    /// <summary>
    /// 统计「模型的真实回复」条数 + 总条数。
    ///
    /// 判据（2026-10-24 04:10）：
    ///   有 NpcRawOutput 且不是配置开场白 → 算一条真实回复
    ///   一条都没有 → 这个 NPC 从没成功对话过 → 第 1 轮 → 该补写
    ///
    /// 为什么不用「ChatMessages 条数」：开场白删没删、系统消息多少都会影响条数，
    /// 而「有没有模型回复」是语义级的判断，不受这些干扰。
    /// </summary>
    private static (int Replies, int Total) CountModelReplies(object npc)
    {
        try
        {
            if (npc == null) return (0, -1);
            var p = npc.GetType().GetProperty("ChatMessages",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var list = p?.GetValue(npc);
            if (list == null) return (0, -1);

            int total;
            if (list is System.Collections.IList il0) total = il0.Count;
            else
            {
                var cp = list.GetType().GetProperty("Count",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                total = cp == null ? -1 : Convert.ToInt32(cp.GetValue(list));
            }
            if (total <= 0) return (0, total);

            var replies = 0;
            for (var i = 0; i < total; i++)
            {
                object m;
                try
                {
                    if (list is System.Collections.IList il1) m = il1[i];
                    else m = list.GetType().GetProperty("Item")?.GetValue(list, new object[] { i });
                }
                catch { continue; }
                if (m == null) continue;

                var raw = GetStr(m, "NpcRawOutput");
                if (string.IsNullOrEmpty(raw)) continue;

                var text = GetStr(m, "MessageText") ?? "";
                if (GreetingFilter.IsConfiguredOpening(npc, text)) continue;   // 开场白不算真实回复
                replies++;
            }
            return (replies, total);
        }
        catch { return (0, -1); }
    }

    private static string GetStr(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var pi = obj.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            return pi?.GetValue(obj)?.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// 判断这次回调是不是「游戏配置里的开场白」。
    ///
    /// 2026-10-24 16:40（外部审计 F6）：必须在 Process 之前调用，
    /// 因为识别的目的是「完全不碰」，一旦先 Process 就已经改了内容。
    /// </summary>
    private static bool IsOpeningMessage(object npc, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            if (GreetingFilter.IsConfiguredOpening(npc, text)) return true;
            // ★ 2026-10-26：模型把开场白复述一遍（和游戏配置不完全一致）也要认出来。
            //   否则会被当成正常回复写进记忆 → 之后每次检索都注入 → 反复提同一段开场白。
            return GreetingFilter.LooksLikeKnownOpening(npc, text);
        }
        catch { return false; }
    }

    /// <summary>
    /// 读 NpcModel.ChatMessages 的条数（Il2Cpp 列表要用反射）。
    /// 用途：判断是不是「第一轮对话」——只有第一轮才补写台本。
    /// </summary>
    private static int CountChatMessages(object npc)
    {
        try
        {
            if (npc == null) return -1;
            var p = npc.GetType().GetProperty("ChatMessages",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var list = p?.GetValue(npc);
            if (list == null) return -1;
            if (list is System.Collections.IList il) return il.Count;
            var cp = list.GetType().GetProperty("Count",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            return cp == null ? -1 : Convert.ToInt32(cp.GetValue(list));
        }
        catch { return -1; }
    }

    [HarmonyPrefix]
    internal static void Prefix(object __instance, ref string messageText, ref string npcRawOutput, int promptTurnId)
    {
        try
        {
            lock (Gate)
            {
                DetectFormat(npcRawOutput);
                var rawMessageText = messageText;

                // ★★ 2026-10-24 16:40 修正（外部审计 F6）：开场白识别必须【提前到 Process 之前】。
                //   原来的顺序是 Process → 折叠空行/裁段 → messageText 赋值 → 才判断开场白，
                //   结果：识别成功时 messageText 已经被改过了，raw.content 还是原文，
                //   函数直接 return → 既没保留开场白原文，也没同步这次改动（版本分叉）。
                //
                // ★★ 2026-10-24 16:50 修正（外部审计 F5）：先【消费】资格，再判断开场白，
                //   归还时必须带同一枚代次 —— 杜绝「凭空创建资格」「A/B 串资格」。
                var cfgIdForRound = Memory.MemoryEngine.GetNpcCfgId(__instance);
                var consumedFirstRound = GreetingFilter.ConsumeFirstRound(cfgIdForRound, out var firstRoundGen);
                var isFirstRound = consumedFirstRound || PromptConfig.FallbackAlways;

                if (IsOpeningMessage(__instance, rawMessageText))
                {
                    _openingInThisCall = true;   // 交给 Postfix 决定删不删
                    // 开场白：一个字节都不碰。只把【真正取出的那枚】资格还回去（审计 F5）
                    if (consumedFirstRound)
                        GreetingFilter.RestoreFirstRound(cfgIdForRound, firstRoundGen);
                    BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                        .LogInfo($"[输出] 开场白：完全不碰（npc={cfgIdForRound}，{Len(rawMessageText)} 字原样保留" +
                                 $"{(consumedFirstRound ? "，第一轮资格已归还" : "，本次无资格")}）");
                    return;
                }

                var processed = Process(messageText);

                // ★ dump 是诊断副作用，绝不能阻断核心处理（审计 F9）
                DumpSafe(messageText, processed.Text, npcRawOutput, promptTurnId);

                messageText = processed.Text;

                // ★ 关键：TTS 等下游 MOD 会从 npcRawOutput 里【重新解析】content，
                //   如果我们只改 messageText，它们读到的还是原文 → 语音和显示对不上。
                //   所以必须把 raw 里的 content 也同步成同一份文本。
                //   cutApplied 传下去：裁过段就必须重建台本（审计 F1）。
                // ★ 2026-10-24 02:35：只在【第一轮】补写台本。
                //   实测（兜底关闭对照实验）：
                //     npc=140003  4 轮全吐 ✓
                //     npc=140201  2 轮全吐 ✓
                //     → 模型从第 2 轮起稳定吐 a1ttsv2，我们继续补写反而破坏 TTS 的 recovery
                //     → 补写的价值只在「第一轮模型还没学会」那一刻
                // ★ 2026-10-24 04:10：判据改成「这个 NPC 有没有过模型的真实回复」
                //   旧写法「chatCount <= 3」不够准（开场白删没删会影响条数）。
                //   新写法：数「有 NpcRawOutput 且不是配置开场白」的消息，
                //   一条都没有 = 从没成功对话过 = 第 1 轮 → 补写。
                // ★ 2026-10-24 04:30：判据改成「本次进入后的第一轮」
                //   为什么不用「有没有模型回复」：老 NPC 有几十条历史，那条判据永远为 false，
                //   但它的历史全是纯文本 → 模型第一轮照样被压着不吐 → 没人兜底 → 哑。
                //   现在由 StartFreeChatAsync 置一个标记，输出侧取用一次。
                var (replyCount, chatCount) = CountModelReplies(__instance);
                if (!isFirstRound && chatCount > 0)
                {
                    // 只在异常情况打一次，避免刷屏；正常轮次由 SyncRawContent 里的日志覆盖
                }
                var synced = SyncRawContent(npcRawOutput, processed.Text, processed.CutApplied,
                                    processed.DialogueCut, isFirstRound);
                if (!ReferenceEquals(synced, npcRawOutput))
                {
                    npcRawOutput = synced;
                    // 日志在 SyncRawContent 内部按「改了 content」还是「只补了台本」分别打，
                    // 这里不再笼统地说「已同步 content」（会误导成本文被改动）
                }

                // ★ 阶段 2：把「玩家话 + 角色话」写成一条 chunk
                var (jid, juser) = MemoryEngine.PeekPending();
                MemoryEngine.FlushTurn(messageText, promptTurnId);

                // ★ 2026-10-27：自动好感度判定（读配置：sync 当场加 / async 迟一轮）
                try { BuWenFanChen.IntimacyJudge.Judge(jid, juser, messageText, __instance); } catch { }

                // ★ 2026-10-26：语音诊断——每轮记一行，专门抓「没语音」的问题。
                //   玩家反馈没声音时，把 语音诊断.log 发来就能看到是
                //   「模型没吐台本」还是「吐了但被我们改坏」还是「检测不到 TTS」。
                try { WriteVoiceDiag(cfgIdForRound, npcRawOutput, messageText, promptTurnId); } catch { }
            }
        }
        catch (Exception ex)
        {
            // 输出处理绝不能崩游戏，但也不能完全静默：
            // ★ 2026-10-26 加日志——如果这里抛异常，后面的 FlushTurn 就不会执行，
            //   表现为「对话一条都不写」。以前这里全吞，完全看不到。
            if (!_prefixErrorLogged)
            {
                _prefixErrorLogged = true;
                try
                {
                    BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                        .LogError($"[输出] 本轮处理异常，对话可能不写：{ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 语音诊断：每轮写一行到 语音诊断.log（数据根目录下）。
    /// 目的：玩家反馈「没语音」时能直接看出断在哪一环：
    ///   TTS=无          → 没检测到语音 MOD
    ///   a1ttsv2=None    → 模型这轮没给台本（也没人补）
    ///   a1ttsv2=Invalid → 给了但不符合 TTS 契约
    ///   a1ttsv2=Valid   → 台本正常，问题在 TTS 侧
    /// </summary>
    private static void WriteVoiceDiag(string npcId, string raw, string outText, int turn)
    {
        try
        {
            var root = BuWenFanChen.Memory.MemoryPaths.Root;
            if (string.IsNullOrEmpty(root)) return;
            var path = System.IO.Path.Combine(root, "语音诊断.log");

            var state = "?";
            var segCount = 0;
            try
            {
                var obj = BuWenFanChen.Output.ResponseNormalizer.ParseJson(raw);
                if (obj != null)
                {
                    state = BuWenFanChen.Output.ResponseNormalizer.InspectA1TtsV2(obj, out _).ToString();
                    var segs = (obj["a1ttsv2"] as System.Text.Json.Nodes.JsonObject)?["segments"]?.AsArray();
                    segCount = segs?.Count ?? 0;
                }
                else state = "ParseFail";
            }
            catch { state = "Ex"; }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | turn={turn} | npc={npcId} | "
                     + $"TTS={(TtsPresent() ? "有" : "无")} | a1ttsv2={state} | segments={segCount} | "
                     + $"raw={(raw?.Length ?? 0)}字 | out={(outText?.Length ?? 0)}字";
            lock (_diagLock)
                System.IO.File.AppendAllText(path, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    private static readonly object _diagLock = new();

    private static bool _prefixErrorLogged;

    /// <summary>降级检测：统计模型输出是不是合法 JSON，失败率高则告警。</summary>
    private static void DetectFormat(string npcRawOutput)
    {
        var parsed = ResponseNormalizer.ParseJson(npcRawOutput);
        _windowTotal++;
        if (parsed == null) _windowFail++;

        if (_windowTotal >= WINDOW)
        {
            var rate = (double)_windowFail / _windowTotal;
            if (rate > FAIL_THRESHOLD)
            {
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogWarning($"模型格式支持较差：最近 {_windowTotal} 轮 JSON 失败率 {rate:P0}，建议切换模型或检查接口");
            }
            _windowTotal = 0;
            _windowFail = 0;
        }
    }

    /// <summary>
    /// 超过上限多少倍才真删（C 方案）。
    /// 2026-10-23 03:32 主人定：1.5 → 2.0。
    /// 原因：台词上限压回 80 后，若还用 1.5（硬删线 120），138 字的正常台词又会被整段删。
    ///       2.0 后台词硬删线 160，日常对话安全，只有模型失控才动刀。
    /// </summary>
    private static double HardRatio => PromptConfig.ReplyLenHardRatio;

    /// <summary>
    /// 输出侧文本处理。返回 (处理后文本, 是否发生过裁段)。
    /// ★ cutApplied 很关键：一旦裁过段，模型给的 a1ttsv2 就与正文不对应了，
    ///   SyncRawContent 必须据此重建台本，否则 TTS 会朗读已经被删掉的台词。
    /// </summary>
    private static (string Text, bool CutApplied, bool DialogueCut) Process(string messageText)
    {
        if (string.IsNullOrEmpty(messageText)) return (messageText, false, false);
        var log = BepInEx.Logging.Logger.CreateLogSource("不问凡尘");

        // ⓪ ★ v1 帧兼容（2026-10-24 20:00，主人：还有人用旧版语音 MOD 要兼容）
        //   content 里可能夹着 <a1tts_v1>…</a1tts_v1> 标签。
        //   先剥出来，只处理纯文本，最后把帧原样拼回末尾。
        //   否则折叠空行 / 裁段会破坏帧（帧里的换行被折叠、标签被当旁白统计字数）。
        var (pureText, v1Frame) = ResponseNormalizer.SplitFrame(messageText);

        // ① 折叠空行（结构类，程序保证）
        var t = ResponseNormalizer.CollapseBlankLines(pureText);

        // ② 通道裁段
        //   ★ 2026-10-24 17:10：只裁旁白，台词【一律不裁】（cutDialogue: false）。
        //     原因：台词是语音 MOD 要朗读的内容，删掉会让它判定「显示内容变了」
        //     而拒绝朗读（display_spoken_content_changed，实测第 2/3 轮全哑）。
        //     旁白不朗读，删它不影响语音，也不会让它的一致性检查失败。
        //     台词超长只记日志，靠提示词控制。
        var (limited, dropNarr, dropDial) = ResponseNormalizer.EnforceChannelLimits(
            t, _maxNarrationChars, _maxDialogueChars, HardRatio, cutDialogue: false);
        var cutApplied = (dropNarr > 0 || dropDial > 0);
        // ★ 2026-10-24 17:15：单独记「台词有没有被裁」。
        //   台词没被裁 → 台本不需要重建（台词没变，台本自然还对得上），
        //   这样就不会触发语音 MOD 的 display_spoken_content_changed。
        var dialogueCut = dropDial > 0;
        if (cutApplied)
            log.LogInfo($"[输出] 超 {HardRatio:P0} 才裁段：旁白 -{dropNarr} 段 / 台词 -{dropDial} 段");

        // ③ 软警告：超过上限但没到硬删阈值（不删，只记）
        if (!cutApplied)
        {
            var narr = ResponseNormalizer.CountNarration(limited);
            var dial = ResponseNormalizer.CountDialogue(limited);
            if (narr > _maxNarrationChars)
                log.LogInfo($"[输出] 旁白略超未删：{narr}/{_maxNarrationChars} 字");
            if (dial > _maxDialogueChars)
                log.LogInfo($"[输出] 台词超限未删（不裁台词，避免 TTS 一致性检查失败）：{dial}/{_maxDialogueChars} 字");
        }

        // ★ 2026-10-26：下限检测（只记日志，不自动补写——补写会破坏语音台本）
        var loFloor = PromptConfig.ReplyLenLo;
        if (loFloor > 0)
        {
            var actual = ResponseNormalizer.CountNarration(limited) + ResponseNormalizer.CountDialogue(limited);
            if (actual < loFloor)
                log.LogInfo($"[输出] 低于下限：{actual}/{loFloor} 字（下限靠提示词约束，输出侧不补写）");
        }

        // ④ 总上限切分（句界，不切括号/引号/帧内）
        //    ★ 软上限 _maxContentChars 是提示词目标；真正的总硬截在 × HardRatio × 1.5
        //      （2.0 → 200×2×1.5 = 600，故意 3 倍兜底，尽量避免动到台词）
        // ★ 2026-10-24 17:10：总截断阈值抬高到 3 倍（600 字）。
        //   因为台词不再裁，只裁旁白后总长已经控住了；
        //   这个截断只作为极端兜底，尽量避免动到台词。
        var hardContent = (int)Math.Ceiling(_maxContentChars * HardRatio * 1.5);
        var final = ResponseNormalizer.ClipContent(limited, hardContent);

        // 总截断也算「改动了正文」；因为它可能在句界截到台词，保守按「台词被裁」处理
        if (final != limited)
        {
            cutApplied = true;
            dialogueCut = true;
        }

        // ⑤ v1 帧拼回末尾（帧内容一字未动）
        if (v1Frame != null)
            final += v1Frame;

        return (final, cutApplied, dialogueCut);
    }

    /// <summary>
    /// 把 npcRawOutput 里的 content 同步成处理后的文本，并维持 a1ttsv2 的可用性。
    ///
    /// 依据 2026-10-23 GPT 审计报告修复以下缺陷：
    ///   F1  裁过段必须重建台本（否则 TTS 朗读已删除的台词）
    ///   F2  支持字符串化台本；别名冲突不再强行加小写键
    ///   F3  可用性判断改成严格契约校验（原来「有一条 text 就算可用」过松）
    ///   F4  显式 null 一律保留（尊重模型「本轮不说话」的决定）
    ///
    /// 决策表：
    ///   裁段 | 状态            | 动作
    ///   -----|-----------------|------------------------------------
    ///   否   | Absent          | 从正文补写
    ///   否   | ExplicitNull    | 保留 null，只记日志
    ///   否   | Valid           | 保留模型版（带 tag，更好）
    ///   否   | StringForm      | 解码成对象写入（TTS 两种都认，对象更稳）
    ///   否   | Invalid         | 丢弃并重建（留着坏的会让 TTS 直接拒）
    ///   否   | AliasConflict   | 不动，只记日志并跳过
    ///   是   | Valid/StringForm| ★重建（正文已变，旧台本对应的是旧正文）
    ///   是   | Absent          | 补写
    ///   是   | ExplicitNull    | 保留 null，记「正文已裁但模型说不说话」冲突
    ///   是   | Invalid         | 重建
    ///   是   | AliasConflict   | 不动，只记日志
    /// </summary>
    /// <summary>
    /// 把 npcRawOutput 里的 content 同步成处理后的文本，并维持 a1ttsv2 的可用性。
    ///
    /// ★★ 2026-10-24 16:50 重写（修复外部审计 F1：content 与台本版本分叉）★★
    ///
    /// 原 bug 的根子：原来「改 content」和「处理台本」不是原子操作，而且提前返回分支
    /// 在改完 content 后直接 return，导致 content 是新句、segments 还是原句 → 分叉。
    ///
    /// 新原则（两条，也是审计给的方向）：
    ///   ① 只有【裁段】（真的删了台词）才改 raw.content；
    ///      折叠空行等排版改动只改 messageText（显示），不动 raw.content ——
    ///      对白没变，台本也不用变，从根上就不会分叉。
    ///   ② 一旦改了 raw.content（裁段），台本【必须同步跟随】：
    ///      能重建就重建，不能重建就清除 —— 旧台本对应已删内容，绝不能留。
    ///
    /// 补写开关（fallbackEnabled / allowFallback）只影响「缺台本时要不要主动生成」，
    /// 不影响「已有台本必须跟随正文」—— 这两件事审计点名要求分开。
    /// </summary>
    private static string SyncRawContent(string raw, string newContent, bool cutApplied,
                                         bool dialogueCut, bool allowFallback = true)
    {
        if (string.IsNullOrEmpty(raw) || newContent == null) return raw;
        var log = BepInEx.Logging.Logger.CreateLogSource("不问凡尘");
        try
        {
            System.Text.Json.Nodes.JsonNode node;
            try
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(raw);
            }
            catch (Exception pe)
            {
                // ★ 2026-10-26：严格解析失败时，先走 ResponseNormalizer.ParseJson 的修补能力
                //   （尾逗号/单引号/中文引号，特别是「模型把 <a1tts_v2>{…}</a1tts_v2> 塞进 content
                //   且内层引号没转义」这种会被花括号扫描带偏的畸形输出）。
                //   原来这里直接 return raw → 游戏自己解析失败 → content 落到兜底文案
                //   （玩家看到的「连续几轮重复同一句」就是这么来的）。
                var repaired = BuWenFanChen.Output.ResponseNormalizer.ParseJson(raw);
                if (repaired == null)
                {
                    log.LogWarning($"[输出] raw JSON 解析失败（{pe.GetType().Name}），content/台本均未同步");
                    return raw;
                }
                node = repaired;
                log.LogInfo("[输出] raw JSON 有瑕疵，已用修补解析救回（content 保住）");
            }
            if (node is not System.Text.Json.Nodes.JsonObject obj) return raw;
            if (!obj.ContainsKey("content")) return raw;

            var oldContent = obj["content"]?.GetValue<string>() ?? "";
            // ★ F1 核心：只有裁段才认为 content 变了（折叠空行不改 raw.content）
            var contentChanged = cutApplied && oldContent != newContent;

            // ============ 分支一：裁段（content 改了）============
            if (contentChanged)
            {
                obj["content"] = newContent;

                // ★★ 2026-10-24 17:15（修 display_spoken_content_changed）：
                //   如果【只裁了旁白、台词一个字没动】，台本根本不需要重建 ——
                //   台本里本来就只有台词，台词没变，台本自然还对得上。
                //   这种情况下重建反而可能改变分段、制造和语音 MOD 缓存的不一致。
                if (!dialogueCut)
                {
                    log.LogInfo($"[输出] content 已改（只裁旁白：{oldContent.Length} → {newContent.Length} 字），" +
                                "台词未动 → 台本保持原样（不重建）");
                    return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    });
                }

                log.LogInfo($"[输出] content 已改（裁了台词：{oldContent.Length} → {newContent.Length} 字），台本同步跟随");

                var v2RawLen = BuWenFanChen.Output.ResponseNormalizer.MeasureA1TtsV2ValueLength(raw);
                var state = BuWenFanChen.Output.ResponseNormalizer.InspectA1TtsV2(obj, out var reason, v2RawLen);

                switch (state)
                {
                    case A1State.AliasConflict:
                        // 冲突不动，留给消费端拒绝；至少不额外制造新冲突
                        log.LogInfo($"[输出] a1ttsv2：裁段但别名冲突（{reason}），台本不重建");
                        break;

                    case A1State.ExplicitNull:
                        // 模型明确「本轮不说话」，裁段后仍保留 null
                        log.LogInfo("[输出] a1ttsv2：裁段但模型显式 null，保留 null");
                        break;

                    default:
                        // Valid / StringForm / Invalid / Absent：旧台本对应旧正文 → 重建或清除
                        var rebuilt = TryBuild(obj, newContent, out var segCount);
                        if (rebuilt)
                        {
                            log.LogInfo($"[输出] a1ttsv2：裁段后重建（原状态 {state}，现 {segCount} 段）");
                        }
                        else
                        {
                            // 重建失败（生成器读不出完整对白）→ 清除旧台本，绝不读已删内容
                            obj.Remove("a1ttsv2");
                            obj.Remove("A1ttsv2");
                            log.LogInfo($"[输出] a1ttsv2：裁段后无法重建（原状态 {state}），已清除旧台本避免读已删内容");
                        }
                        break;
                }

                return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }

            // ============ 分支二：没裁段（content 不动，台本默认也不动）============
            log.LogInfo($"[输出] content 未变（{newContent.Length} 字），raw.content 不写回（折叠空行只作用于显示）");

            // 三个开关：没 TTS / 兜底关 / 非第一轮 → 什么都不做
            // content 没变、台本不动 → 完全一致，安全返回原串
            if (!TtsPresent())
            {
                log.LogInfo("[输出] 未检测到语音 MOD，台本不处理");
                return raw;
            }
            if (!PromptConfig.FallbackEnabled)
            {
                log.LogInfo("[输出] 兜底已关闭（fallbackEnabled=false），台本交给 TTS 自己处理");
                return raw;
            }
            if (!allowFallback)
            {
                var st0 = BuWenFanChen.Output.ResponseNormalizer.InspectA1TtsV2(obj, out var rs0);
                log.LogInfo($"[输出] 非第一轮 → 不补写台本（本捕获点 {st0}" +
                            $"{(rs0 == null ? "" : "，" + rs0)}）");
                return raw;
            }

            // 正常：处理台本（只补缺失/修坏的，不动已有的有效台本）
            var v2RawLen2 = BuWenFanChen.Output.ResponseNormalizer.MeasureA1TtsV2ValueLength(raw);
            var state2 = BuWenFanChen.Output.ResponseNormalizer.InspectA1TtsV2(obj, out var reason2, v2RawLen2);
            string action = null;
            var builtV2 = false;
            var cleared = false;

            switch (state2)
            {
                case A1State.AliasConflict:
                    action = "别名冲突（a1ttsv2 + A1ttsv2 同时存在），不动并跳过语音";
                    break;

                case A1State.ExplicitNull:
                    action = "显式 null，保留（本轮不说话）";
                    break;

                case A1State.Valid:
                    {
                        var v2obj = obj["a1ttsv2"] as System.Text.Json.Nodes.JsonObject;
                        var segs = v2obj?["segments"]?.AsArray();
                        var segTotal = segs?.Count ?? 0;
                        var tagCount = 0;
                        if (segs != null)
                        {
                            foreach (var one in segs)
                            {
                                var tg = (one as System.Text.Json.Nodes.JsonObject)?["tag"]?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(tg)) tagCount++;
                            }
                        }
                        // ★ 2026-10-24 17:00：记录模型自己吐的 instruct（长度 + 前 40 字）
                        //   用途：确认模型吐的 instruct 质量，判断我们的 BuildInstruct 有没有必要干预
                        var insStr = "";
                        try { insStr = v2obj?["instruct"]?.GetValue<string>() ?? ""; } catch { }
                        action = $"模型自带有效（{segTotal} 段，{tagCount} 段带 tag，" +
                                 $"instruct {insStr.Length} 字「{(insStr.Length > 40 ? insStr[..40] + "…" : insStr)}」）";
                    }
                    break;

                case A1State.StringForm:
                    {
                        var decoded = TryDecodeStringForm(obj);
                        action = decoded ? "字符串化台本已解码为对象" : "字符串化台本存在但无法解码，保留原样";
                        if (decoded) builtV2 = true;
                    }
                    break;

                case A1State.Absent:
                    {
                        var built = TryBuild(obj, newContent, out var segCount);
                        if (built)
                        {
                            builtV2 = true;
                            action = $"补写（本捕获点 absent，{segCount} 段台词；上游可能已被游戏清洗，不代表模型没吐）";
                        }
                        else
                        {
                            action = "无台词可补（本轮可能只有旁白）";
                        }
                    }
                    break;

                case A1State.Invalid:
                    {
                        var built = TryBuild(obj, newContent, out var segCount);
                        if (built)
                        {
                            builtV2 = true;
                            action = $"本捕获点无效（{reason2}）→ 重建（{segCount} 段）";
                        }
                        else
                        {
                            obj.Remove("a1ttsv2");
                            obj.Remove("A1ttsv2");
                            cleared = true;
                            action = $"本捕获点无效（{reason2}）且无可重建台词 → 已清除";
                        }
                    }
                    break;
            }

            if (action != null) log.LogInfo($"[输出] a1ttsv2：{action}");

            // 只有真正改了对象（补写/重建/解码/清除）才返回新串
            if (!builtV2 && !cleared) return raw;

            return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (Exception e)
        {
            log.LogWarning($"[输出] SyncRawContent 异常（{e.GetType().Name}），raw 原样返回");
            return raw;
        }
    }

    /// <summary>从正文重建 a1ttsv2 并写入 obj。返回是否成功。</summary>
    private static bool TryBuild(System.Text.Json.Nodes.JsonObject obj, string content, out int segCount)
    {
        segCount = 0;
        try
        {
            var emotion = obj["emotion"]?.GetValue<string>() ?? "";
            var v2 = BuWenFanChen.Output.ResponseNormalizer.BuildA1TtsV2(content, emotion);
            if (string.IsNullOrEmpty(v2)) return false;
            var built = System.Text.Json.Nodes.JsonNode.Parse(v2);
            // 自检：自己造的东西也必须过同一套契约校验，否则等于把坏台本塞给 TTS
            if (!BuWenFanChen.Output.ResponseNormalizer.IsValidA1TtsV2(built, out _)) return false;

            // ★★ 2026-10-24 16:45 修正（外部审计 F2）：
            //   原来只写小写 a1ttsv2，没清掉已有的 A1ttsv2 →
            //   裁剪后重建会输出【两个键】→ 消费端直接拒 alias_conflict:a1ttsv2。
            //   现在：重建即统一成唯一规范键（小写），先删大写再写。
            obj.Remove("A1ttsv2");
            obj["a1ttsv2"] = built;
            segCount = (built as System.Text.Json.Nodes.JsonObject)?["segments"]?.AsArray()?.Count ?? 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把字符串化台本解码成对象写回。返回是否成功。</summary>
    private static bool TryDecodeStringForm(System.Text.Json.Nodes.JsonObject obj)
    {
        try
        {
            if (!obj.TryGetPropertyValue("a1ttsv2", out var n) || n is not System.Text.Json.Nodes.JsonValue v) return false;
            if (!v.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str)) return false;
            var inner = System.Text.Json.Nodes.JsonNode.Parse(str);
            if (!BuWenFanChen.Output.ResponseNormalizer.IsValidA1TtsV2(inner, out _)) return false;
            obj["a1ttsv2"] = inner;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// dump 是诊断副作用，失败绝不能阻断核心处理（审计 F9）。
    /// </summary>
    private static void DumpSafe(string messageText, string processed, string npcRawOutput, int promptTurnId)
    {
        // 2026-10-24：默认不写 dump（含玩家对话明文，且会无限堆积）
        if (!PromptConfig.DumpEnabled) return;
        try
        {
            Dump(messageText, processed, npcRawOutput, promptTurnId);
        }
        catch (Exception e)
        {
            try
            {
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogWarning($"[输出] dump 写入失败（{e.GetType().Name}），已跳过，不影响核心处理");
            }
            catch { }
        }
    }

    private static void Dump(string messageText, string processed, string npcRawOutput, int promptTurnId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== 输出侧 dump ==========");
        sb.AppendLine($"时间: {DateTime.Now:HH:mm:ss.fff}  turn={promptTurnId}");
        sb.AppendLine($"messageText 原始: {Len(messageText)} 字 / 空段 {CountBlank(messageText)} 处");
        sb.AppendLine($"messageText 处理后: {Len(processed)} 字 / 空段 {CountBlank(processed)} 处");
        sb.AppendLine("--- 原始全文 ---");
        sb.AppendLine(messageText ?? "null");
        sb.AppendLine("--- 处理后全文（实际发给游戏）---");
        sb.AppendLine(processed ?? "null");
        sb.AppendLine($"npcRawOutput: {Len(npcRawOutput)} 字");
        sb.AppendLine("--- raw 里的 content 原始值 ---");
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(npcRawOutput ?? "");
            var rawContent = (node as System.Text.Json.Nodes.JsonObject)?["content"]?.GetValue<string>() ?? "";
            sb.AppendLine($"raw.content 长度: {rawContent.Length} 字");
            sb.AppendLine(rawContent.Length == 0 ? "(空或读不到)" : rawContent);
            sb.AppendLine($"与 messageText 一致: {rawContent == messageText}");
        }
        catch (Exception e)
        {
            sb.AppendLine($"raw 解析失败: {e.GetType().Name}");
        }
        sb.AppendLine("========== 结束 ==========");

        var path = System.IO.Path.Combine(GetDir(), $"out_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        DumpPruner.Prune(GetDir(), PromptConfig.DumpKeep);
    }

    /// <summary>统计连续换行（空段）出现次数。</summary>
    private static int CountBlank(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var n = 0;
        for (var i = 0; i < s.Length - 1; i++)
            if (s[i] == '\n' && s[i + 1] == '\n') n++;
        return n;
    }

    private static int Len(string s) => s?.Length ?? -1;

    private static string Truncate(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "null";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string GetDir()
    {
        if (_dir != null) return _dir;
        var pluginDir = System.IO.Path.GetDirectoryName(typeof(AddNpcChatMessagePatch).Assembly.Location);
        _dir = System.IO.Path.Combine(pluginDir, "dump");
        System.IO.Directory.CreateDirectory(_dir);
        return _dir;
    }

    /// <summary>
    /// 开场白的 Postfix 处理（2026-10-24 21:10）。
    ///
    /// 为什么放这里：开场白这条消息在 StartFreeChatAsync / RestoreDialogueHistory 阶段
    /// 的 MessageText 还是空的，那时删不掉（日志："跳过：末条文本为空"）。
    /// 等走到 AddNpcChatMessage 时文本已经有了，所以在这之后的 Postfix 判断：
    ///   老 NPC（已有真实对话）→ 把刚加进来的开场白删掉，界面不显示
    ///   新 NPC（没有真实对话）→ 保留，作为首次引导
    /// </summary>
    [HarmonyPostfix]
    internal static void Postfix(object __instance)
    {
        if (!_openingInThisCall) return;
        _openingInThisCall = false;
        try
        {
            var (replyCount, _) = CountModelReplies(__instance);
            if (replyCount > 0)
            {
                // 已有真实对话 → 这个 NPC 聊过了，开场白多余
                GreetingFilter.RemoveLastPublic(__instance);
            }
            else
            {
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogInfo("[开场白] Postfix 保留（新 NPC，没有真实对话 → 首次引导）");
            }
        }
        catch (Exception e)
        {
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                .LogWarning($"[开场白] Postfix 异常 {e.GetType().Name}: {e.Message}");
        }
    }
}
