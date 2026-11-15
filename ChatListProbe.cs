using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen;

/// <summary>
/// 开场白过滤（正式版，2026-10-24 01:05 定案）。
///
/// 调查过程（都试过、都不行）：
///   ✗ AddFirstGreetingMessage        从没被调用（死代码）
///   ✗ OnReceiveChatMessage           开场白不经过它
///   ✗ PickFirstGreetingFromConfig    从没被调用
///   ✗ Panel 里找 NpcModel            没有该字段（只有 isWrapped/pooledPtr）
///   ✗ 靠 NpcRawOutput 为空判断       开场白也有 raw（97 字）
///
/// 最终方案：
///   · 时机：Game.Model.NpcModel.StartFreeChatAsync 的 Prefix
///           （此时能拿到 NpcModel，且 OnShow 还没渲染）
///   · 判据：NpcModel.IsConfiguredOpening(text) —— 游戏自己的判断函数
///   · 保护：只有 1 条时保留（那是真正的首次对话引导）
///
/// 实测（npc=100121，42 条历史）：
///   IsConfiguredOpening=True → 已移除 → 42 → 41 条 ✓
/// </summary>
[HarmonyPatch(typeof(Game.Model.NpcModel), "StartFreeChatAsync")]
internal static class GreetingFilter
{
    /// <summary>
    /// NpcModel 引用缓存（key = cfgId）。
    /// 用途：RestoreDialogueHistory 那个位置拿不到 NpcModel（Panel 没这字段），
    /// 但那里正是 UI 开始渲染的地方。缓存上次的引用后，
    /// 老 NPC 重进时就能在渲染【之前】删掉开场白，界面上不会出现。
    /// </summary>
    private static readonly Dictionary<string, object> NpcCache = new();

    /// <summary>
    /// 每个 NPC 待消费的「本次进入第一轮」资格，值是单调递增的代次。
    ///
    /// ★ 2026-10-24 16:50 改（修复外部审计 F5）：
    ///   原来是一个全局字符串单槽，有两个 bug：
    ///     复现一：开场白回调会无条件 Restore，凭空创建新资格 → 下一条回复被多补一轮
    ///     复现二：进入 A → 进入 B → 迟到 A 开场白，全局单槽从 B 被改回 A，B 丢失资格
    ///   改成「按 NPC 存代次」：
    ///     · 每个 NPC 资格独立，不会串
    ///     · 归还必须带【同一枚代次】，不会凭空创建
    /// </summary>
    private static readonly Dictionary<string, long> PendingGeneration = new();
    private static long _generationCounter;

    /// <summary>给某 NPC 发一枚「本次进入第一轮」资格（代次递增），由 StartFreeChatAsync 调用。</summary>
    public static void MarkFirstRound(string cfgId)
    {
        if (string.IsNullOrEmpty(cfgId)) return;
        PendingGeneration[cfgId] = ++_generationCounter;
    }

    /// <summary>
    /// 取用某 NPC 的「本次进入第一轮」资格。取到返回 true 并输出代次，取走即删。
    /// </summary>
    public static bool ConsumeFirstRound(string cfgId, out long generation)
    {
        generation = 0;
        if (string.IsNullOrEmpty(cfgId)) return false;
        if (!PendingGeneration.TryGetValue(cfgId, out generation)) return false;
        PendingGeneration.Remove(cfgId);
        return true;
    }

    /// <summary>
    /// 归还资格。只归还【同一枚代次】——代次不匹配就说明期间已经进入过新会话，
    /// 绝不覆盖，也绝不凭空创建（修复审计 F5 复现一、二）。
    /// </summary>
    public static void RestoreFirstRound(string cfgId, long generation)
    {
        if (string.IsNullOrEmpty(cfgId) || generation == 0) return;
        // 该 NPC 已有更新的代次（期间又进入过）→ 不覆盖旧资格
        if (PendingGeneration.ContainsKey(cfgId)) return;
        PendingGeneration[cfgId] = generation;
    }

    [HarmonyPrefix]
    internal static void Prefix(object __instance)
    {
        try        {
            // 先缓存引用，供 RestoreDialogueHistory（更早的点）复用
            try
            {
                var cid = Memory.MemoryEngine.GetNpcCfgId(__instance);
                if (!string.IsNullOrEmpty(cid))
                {
                    NpcCache[cid] = __instance;
                    // ★ 标记「这个 NPC 刚进入对话」——本次进入后的第一轮要补写
                    MarkFirstRound(cid);

                    // ★ 2026-11-14 主人要求：进对话就更新「当前 NPC」→ 性格面板立刻跟着切角色
                    //   （以前要聊一句、模型回复后才更新，会显示上一个角色的面板）
                    try { BuWenFanChen.Panel.PanelStore.NoteCurrentNpc(cid); } catch { }

                    // ★ 2026-10-25：进对话就抓关系网 + 生成角色世界书
                    //   GameWorld 是从 NpcModel 的属性拿的，所以不用等玩家聊一句
                    try { CompleteBudgetedPatch.CaptureRelations(__instance); } catch { }
                }
            }
            catch { }

            // ★ 2026-10-31：进对话时，先把上一轮「欠下」的开场白删掉。
            //   此时 TTS 没在为那条开场白生成（上一轮早念完了），不会出现
            //   「删掉的消息正好是 TTS 正在生成的那条」→ 取消 → 外部服务卡死。
            try { FlushPendingOpening(__instance); } catch { }

            RemoveOpeningIfNeeded(__instance);
        }
        catch (Exception e)
        {
            Log($"[开场白] 过滤异常（放行）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>取出缓存的 NpcModel 引用（供 RestoreDialogueHistory 那个点用）。</summary>
    internal static bool TryGetCachedNpc(string cfgId, out object npc)
    {
        npc = null;
        if (string.IsNullOrEmpty(cfgId) || NpcCache == null) return false;
        return NpcCache.TryGetValue(cfgId, out npc);
    }

    /// <summary>供外部（GreetingFilterEarly）调用的公开入口。</summary>
    internal static void RemoveOpeningPublic(object npc) => RemoveOpeningIfNeeded(npc);

    // ★ 2026-10-31：开场白「延后删除」账本。
    //   进对话时不删（那条可能正被 TTS 念），记一笔；下次进对话时 TTS 空闲，再真删。
    private static readonly System.Collections.Generic.HashSet<string> _pendingOpeningRemove = new();

    /// <summary>记一笔「这个角色的开场白要删」，等下次进对话再执行。</summary>
    internal static bool MarkPendingRemoval(string cfgId)
    {
        if (string.IsNullOrEmpty(cfgId)) return false;
        lock (_pendingOpeningRemove) _pendingOpeningRemove.Add(cfgId);
        return true;
    }

    /// <summary>进对话时执行欠下的删除（此时 TTS 没在为那条开场白生成）。</summary>
    internal static void FlushPendingOpening(object npc)
    {
        var cfgId = Memory.MemoryEngine.GetNpcCfgId(npc);
        if (string.IsNullOrEmpty(cfgId)) return;

        // ★ 2026-11-10：改成「先检查、后消费账」——
        //   原来一进来就 Remove 掉账，如果这一刻末条还不是开场白（时序靠前、或游戏尚未把开场白加进来），
        //   账就白丢了，下一次也不会再删。现在只有真删成功或确认「开场白已不在」时才清账。
        lock (_pendingOpeningRemove)
        {
            if (!_pendingOpeningRemove.Contains(cfgId)) return;
        }

        var cnt = Count(npc);
        if (cnt < 1) { Log($"[开场白] 欠账保留（npc={cfgId}，没有消息）"); return; }
        var list = ReadChatMessages(npc);
        if (list == null) { Log($"[开场白] 欠账保留（npc={cfgId}，读不到 ChatMessages）"); return; }
        // ★★ 2026-11-10 修正（关键）：开场白不再看末条！
        //   实测（npc=100121）：共 57 条，末条 #56 是系统消息（SystemMarker="## 任务：闲聊"、文本空），
        //   而真正的开场白是 #53，游戏自己给了 IsOpening=True 标记。
        //   所以改成：按 IsOpening 标记定位、按索引删除，位置任意。
        int idx = FindOpeningIndex(list, cnt);
        if (idx < 0)
        {
            lock (_pendingOpeningRemove) _pendingOpeningRemove.Remove(cfgId);
            Log($"[开场白] 欠账清除（npc={cfgId}）：列表里已经没有 IsOpening 开场白了");
            return;
        }
        if (RemoveAt(list, idx))
        {
            lock (_pendingOpeningRemove) _pendingOpeningRemove.Remove(cfgId);
            Log($"[开场白] 欠账已清（npc={cfgId}，删第 {idx} 条，{cnt} → {Count(npc)} 条）");
        }
        else
        {
            Log($"[开场白] ⚠ 欠账删除失败（npc={cfgId}，第 {idx} 条）—— 账保留，下次再试");
        }
    }
    /// <summary>
    /// 移除 ChatMessages 的最后一条（2026-10-24 21:10 加）。
    /// 用途：开场白在 StartFreeChatAsync / RestoreDialogueHistory 时文本还是空的，
    ///     删不掉；等 AddNpcChatMessage 处理时文本已经有了（43 字），
    ///     所以改在那里的 Postfix 删掉刚加进来的那条。
    /// </summary>
    internal static void RemoveLastPublic(object npc)
    {
        try
        {
            var cnt = Count(npc);
            if (cnt < 1) return;
            var list = ReadChatMessages(npc);
            if (list == null) return;
            if (RemoveLast(list, cnt))
                Log($"[开场白] Postfix 移除（npc={Memory.MemoryEngine.GetNpcCfgId(npc)}，{cnt} → {cnt - 1} 条）");
            else
                Log($"[开场白] Postfix 移除失败（npc={Memory.MemoryEngine.GetNpcCfgId(npc)}）");
        }
        catch { }
    }

    private static void RemoveOpeningIfNeeded(object npc)
    {
        var cnt = Count(npc);
        var cfg0 = Memory.MemoryEngine.GetNpcCfgId(npc);

        // 2026-10-24 03:55：每个 return 分支都记日志，否则排查时不知道卡在哪
        if (cnt < 1) { Log($"[开场白] 跳过：条数 {cnt}（npc={cfg0}）"); return; }

        var list = ReadChatMessages(npc);
        if (list == null) { Log($"[开场白] 跳过：读不到 ChatMessages（npc={cfg0}）"); return; }

        // ★★ 2026-11-10 修正：开场白靠游戏的 IsOpening 标记定位（实测在 #53，末条 #56 是系统消息）。
        //   先扫全表找 IsOpening=True；找不到再退回「末条是配置开场白」的老逻辑（兼容旧存档）。
        int openIdx = FindOpeningIndex(list, cnt);
        var openText = "";
        if (openIdx >= 0)
        {
            openText = Get(Item(list, openIdx), "MessageText") ?? "";
        }
        else
        {
            var last = Item(list, cnt - 1);
            var lastText = last == null ? "" : Get(last, "MessageText") ?? "";
            if (!string.IsNullOrEmpty(lastText) && IsConfiguredOpening(npc, lastText))
            {
                openIdx = cnt - 1;
                openText = lastText;
            }
        }

        if (openIdx < 0)
        {
            DumpChatListOnce(list, cnt, cfg0);
            Log($"[开场白] 跳过：列表里没有开场白（npc={cfg0}, {cnt} 条）");
            return;
        }

        // ★ 2026-10-26：记下开场白原文，供「复述识别」和「记忆过滤」用
        RememberOpening(cfg0, openText);

        // ★ 2026-10-24 03:45 修正：不能只看条数。
        //   新 NPC 第一次进入时，列表里已经有开场白（可能还带系统消息），
        //   原来的「cnt >= 2 就删」会把新 NPC 的开场白也删掉。
        //
        //   正确判据：除了开场白之外，还有没有【真实对话】。
        //   有 → 老 NPC（聊过了，开场白多余）→ 删
        //   没有 → 新 NPC（第一次进来）→ 保留，这是首次引导
        // ★★ 2026-10-24 16:40 修正（外部审计 F7）：
        //   原条件只有「文本非空 且 不是配置开场白」——连系统提示文本都会被算成真实对话，
        //   于是「只有系统文本 + 开场白」的新角色，开场白会被误删。
        //   现在只认【有 NpcRawOutput 的模型回复】，系统文本/玩家话都不算。
        var realCount = 0;
        for (var i = 0; i < cnt; i++)
        {
            var m = Item(list, i);
            if (m == null) continue;
            var raw = Get(m, "NpcRawOutput") ?? "";
            if (string.IsNullOrEmpty(raw)) continue;      // 没有 raw → 不是模型回复
            var t = Get(m, "MessageText") ?? "";
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (i == openIdx) continue;                   // 开场白本身不算真实对话
            realCount++;
        }

        var cfgId = Memory.MemoryEngine.GetNpcCfgId(npc);

        if (realCount == 0)
        {
            Log($"[开场白] 保留（npc={cfgId}，{cnt} 条里没有真实对话 → 新 NPC 首次引导）");
            return;
        }

        // ★ 2026-10-31：不在这一刻删 —— 这条开场白可能正被 TTS 生成，
        //   删它会触发「上下文改变 → 取消生成 → 外部服务旧生成未确认结束」，语音从此卡死。
        //   现在改成「记账」：下次进对话（TTS 空闲）再删。
        if (MarkPendingRemoval(cfgId))
        {
            Log($"[开场白] 延后删除（npc={cfgId}，开场白在第 {openIdx} 条，共 {cnt} 条含 {realCount} 条真实对话）："
                + "本次保留让语音念完，下次进对话时再删");
        }
        else
        {
            Log($"[开场白] ⚠ 记账失败（npc={cfgId}，{cnt} 条）");
        }    }

    // ★ 2026-10-26：记住每个 NPC 的开场白原文。
    //   用途①：模型把开场白复述一遍时（和原文不完全一致），也能认出来，不当成正常回复写进记忆；
    //   用途②：已经写成 chunk 的「开场白抄本」，注入前可以过滤掉。
    private static readonly System.Collections.Generic.Dictionary<string, string> _knownOpenings = new();

    internal static void RememberOpening(string cfgId, string text)
    {
        try
        {
            if (string.IsNullOrEmpty(cfgId) || string.IsNullOrWhiteSpace(text)) return;
            _knownOpenings[cfgId] = text;
        }
        catch { }
    }

    internal static string GetKnownOpening(string cfgId)
    {
        try
        {
            return (!string.IsNullOrEmpty(cfgId) && _knownOpenings.TryGetValue(cfgId, out var t)) ? t : null;
        }
        catch { return null; }
    }

    /// <summary>这段文本是不是该 NPC 开场白的复述（前 20 字相同，或互相包含）。</summary>
    internal static bool LooksLikeKnownOpening(object npc, string text)
    {
        try
        {
            var id = Memory.MemoryEngine.GetNpcCfgId(npc);
            var known = GetKnownOpening(id);
            if (string.IsNullOrWhiteSpace(known) || string.IsNullOrWhiteSpace(text)) return false;
            var a = known.Length > 20 ? known.Substring(0, 20) : known;
            var b = text.Length > 20 ? text.Substring(0, 20) : text;
            if (a == b) return true;
            return text.Contains(known) || known.Contains(text);
        }
        catch { return false; }
    }

    internal static bool IsConfiguredOpening(object npc, string text)
    {
        try
        {
            var m = npc.GetType().GetMethod("IsConfiguredOpening",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) return false;
            var r = m.Invoke(npc, new object[] { text });
            return r is bool b && b;
        }
        catch
        {
            return false;   // 调不通就当不是开场白，宁可不删
        }
    }

    // ---------- ChatMessages 列表操作（Il2Cpp 包装要用反射） ----------

    private static int Count(object npc)
    {
        try
        {
            var list = ReadChatMessages(npc);
            if (list == null) return -1;
            if (list is System.Collections.IList il) return il.Count;
            var cp = list.GetType().GetProperty("Count",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return cp == null ? -1 : Convert.ToInt32(cp.GetValue(list));
        }
        catch { return -1; }
    }

    private static object ReadChatMessages(object npc)
    {
        if (npc == null) return null;
        var p = npc.GetType().GetProperty("ChatMessages",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return p?.GetValue(npc);
    }

    private static object Item(object list, int index)
    {
        try
        {
            if (list is System.Collections.IList il) return il[index];
            var ip = list.GetType().GetProperty("Item");
            return ip?.GetValue(list, new object[] { index });
        }
        catch { return null; }
    }

    private static string Get(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj)?.ToString();

            // ★★ 2026-11-10 修（「末条文本为空」导致开场白删不掉）：
            //   旧代码只查【属性】。游戏更新后 ChatMessage.MessageText 很可能从属性变成了【字段】，
            //   于是 GetProperty 返回 null → 读到空字符串 → 判定「末条不是开场白」→ 永远不记账/不删。
            //   现在属性找不到就再查字段（两种形态都能读）。首次走字段路径会打一行日志备查。
            var f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                if (!_getFieldLogged)
                {
                    _getFieldLogged = true;
                    Log($"[开场白] {name} 改从字段读取（游戏更新后它不再是属性）");
                }
                return f.GetValue(obj)?.ToString();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>★ 2026-11-10：「属性找不到、改走字段」只打一次日志</summary>
    private static bool _getFieldLogged;

    /// <summary>★ 2026-11-10：在列表里找游戏标记的开场白（IsOpening=True），返回索引；找不到 -1。</summary>
    private static int FindOpeningIndex(object list, int cnt)
    {
        for (int i = cnt - 1; i >= 0; i--)
        {
            var m = Item(list, i);
            if (m == null) continue;
            var iso = Get(m, "IsOpening") ?? "";
            if (iso.Equals("True", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>★ 2026-11-10：按索引删除列表元素（开场白不在末条）。</summary>
    private static bool RemoveAt(object list, int index)
    {
        try
        {
            if (list is System.Collections.IList il) { il.RemoveAt(index); return true; }
            var rm = list.GetType().GetMethod("RemoveAt", BindingFlags.Public | BindingFlags.Instance);
            if (rm != null) { rm.Invoke(list, new object[] { index }); return true; }
            return false;
        }
        catch { return false; }
    }

    private static bool RemoveLast(object list, int cnt)
    {
        try
        {
            if (list is System.Collections.IList il) { il.RemoveAt(cnt - 1); return true; }
            var rm = list.GetType().GetMethod("RemoveAt",
                BindingFlags.Public | BindingFlags.Instance);
            if (rm != null) { rm.Invoke(list, new object[] { cnt - 1 }); return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>★ 2026-11-10：整个 ChatMessages 列表摘要转储（只打一次）。
    ///   目的：实测末条是系统消息（SystemMarker="## 任务：话题闲聊"、MessageText 空），
    ///   开场白根本不在末条 —— 需要看清：①前 2 条/末 2 条各是什么；
    ///   ②列表里有没有 IsOpening=True 的条目（游戏自己标记的开场白）。</summary>
    private static bool _dumpedChatMessage;
    private static void DumpChatListOnce(object list, int cnt, string cfgId)
    {
        if (_dumpedChatMessage) return;
        _dumpedChatMessage = true;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[开场白·列表] npc={cfgId} 共 {cnt} 条");
            for (int n = 0; n < cnt; n++)
            {
                if (n >= 2 && n < cnt - 2) continue;   // 只打前 2 和末 2
                AppendOne(sb, list, n);
            }
            int openingCount = 0;
            string openingInfo = "";
            for (int n = 0; n < cnt; n++)
            {
                var m = Item(list, n);
                if (m == null) continue;
                var iso = Get(m, "IsOpening") ?? "";
                if (!iso.Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
                openingCount++;
                if (openingCount <= 3)
                {
                    var t = Get(m, "MessageText") ?? "";
                    if (t.Length > 40) t = t[..40] + "…";
                    openingInfo += $"\n[开场白·列表]   ★ IsOpening=True 条目 #{n}：「{t}」";
                }
            }
            sb.Append(openingInfo);
            sb.Append($"\n[开场白·列表] IsOpening=True 共 {openingCount} 条");
            Log(sb.ToString());
        }
        catch { }
    }

    private static void AppendOne(System.Text.StringBuilder sb, object list, int idx)
    {
        try
        {
            var m = Item(list, idx);
            if (m == null) { sb.Append($"\n[开场白·列表]   #{idx} <null>"); return; }
            var t = Get(m, "MessageText") ?? "";
            if (t.Length > 40) t = t[..40] + "…";
            var iso = Get(m, "IsOpening") ?? "";
            var marker = Get(m, "SystemMarker") ?? "";
            if (marker.Length > 20) marker = marker[..20] + "…";
            var raw = Get(m, "NpcRawOutput");
            sb.Append($"\n[开场白·列表]   #{idx} open={iso} raw={(string.IsNullOrEmpty(raw) ? "无" : "有")}"
                      + $" marker=「{marker}」 text=「{t}」");
        }
        catch { }
    }

    private static void Log(string s)
    {
        try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(s); }
        catch { }
    }
}

/// <summary>
/// RestoreDialogueHistory 的 Postfix：在 UI 渲染之前把老 NPC 的开场白删掉。
///
/// 为什么放这里：实测调用顺序是
///   SetDataInternal → RestoreDialogueHistory（开始渲染）→ StartFreeChatAsync → OnShow
/// 在 StartFreeChatAsync 删已经太晚（屏幕上画好的东西不会自己消失）。
/// 这里拿不到 NpcModel，所以用 StartFreeChatAsync 缓存过的引用。
///
/// 效果：
///   新 NPC 第一次进 → 缓存里没有 → 不处理 → 开场白正常保留 ✓
///   老 NPC 重进     → 缓存里有引用 → 渲染前删掉 → 界面上不出现 ✓
///
/// ★ 2026-11-10 修（游戏更新后「删除开场白」失效）：
///   原来写的是 [HarmonyPatch(typeof(Game.NpcDialoguePanel), "RestoreDialogueHistory")]，
///   Harmony 按【无参】查找；游戏更新给这个方法加了参数 → 启动日志
///   「AccessTools.DeclaredMethod: Could not find method ... and parameters」
///   → 整个 patch 挂不上 → "延后删除"标记了却永远没人执行。
///   现在改用 TargetMethod() 遍历该类的所有重载，选参数最少的那个，并把真实签名打进日志。
/// </summary>
[HarmonyPatch]
internal static class GreetingFilterEarly
{
    /// <summary>★ 2026-11-10：按名字遍历重载找目标方法（兼容游戏更新后签名变化），并打印真实签名。</summary>
    private static System.Reflection.MethodBase TargetMethod()
    {
        var t = typeof(Game.NpcDialoguePanel);
        System.Reflection.MethodBase best = null;
        try
        {
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null || m.Name != "RestoreDialogueHistory") continue;
                if (best == null || m.GetParameters().Length < best.GetParameters().Length) best = m;
            }
        }
        catch { }
        try
        {
            var log = BepInEx.Logging.Logger.CreateLogSource("不问凡尘");
            if (best == null)
            {
                log.LogWarning("[开场白] 找不到 NpcDialoguePanel.RestoreDialogueHistory（任何重载都没有）→ 渲染前删除不可用");
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var p in best.GetParameters())
                    sb.Append(p.ParameterType.Name).Append(' ').Append(p.Name).Append(", ");
                log.LogInfo("[开场白] RestoreDialogueHistory 签名（" + best.GetParameters().Length + " 参数）："
                            + sb.ToString().TrimEnd(' ', ','));
            }
        }
        catch { }
        return best;
    }

    [HarmonyPostfix]
    internal static void Postfix()
    {
        try
        {
            var id = CurrentNpcTracker.CurrentCfgId;
            if (string.IsNullOrEmpty(id)) return;
            if (!GreetingFilter.TryGetCachedNpc(id, out var npc) || npc == null) return;

            // ★★ 2026-11-10 关键设计约束（主人明确）：
            //   开场白「延后一轮」是因为 TTS 正在读它，硬删会让 TTS 卡死。
            //   所以这个点**只清上一次欠下的账**，绝不在本轮记账 ——
            //   否则紧接着 StartFreeChatAsync 里的 Flush 会把本轮刚记的账当场删掉，
            //   又变回「TTS 正在念 → 删除 → 取消生成 → 卡死」的老坑。
            //   记账统一留给 StartFreeChatAsync 里的 RemoveOpeningIfNeeded。
            Log($"[开场白] 渲染前清旧账（npc={id}）——本轮不记新账，记账留给 StartFreeChatAsync");
            GreetingFilter.FlushPendingOpening(npc);
        }
        catch { }
    }

    private static void Log(string s)
    {
        try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(s); }
        catch { }
    }
}
