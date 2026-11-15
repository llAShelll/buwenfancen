using System.Reflection;
using System.Text;
using BuWenFanChen.Memory;

namespace BuWenFanChen.History;

/// <summary>
/// ★ 2026-11-05：history 接管（实验性）
///
/// 【问题】游戏每轮给的 history 是「按字符预算截断的滑动窗口」，
///   messages[2] 每轮都变 → 从那条往后全部缓存未命中（实测命中率 20.8%~29%）。
///
/// 【做法】把 history 换成 append-only 的 canon：
///   首轮   canon = 游戏给的全部（直接存对象引用，不新建、不改写）
///   之后   把游戏 history 里 canon 还没的尾部追加上来（指纹比对去重）
///   每轮   __args[6] = canon
///
/// 【落盘】canon 以「role + base64(content)」逐行存到 {MemDir}\canon.jsonl。
///   启动时读回来，尝试用反射重新构造 LlmMessage 接上（能构造 → 重启不失忆）。
///   构造不了就只用内存 canon（日志说明）。
///
/// 【已验证 2026-11-05 实测】
///   · __args[6] 可写（Harmony ref 参数数组）
///   · Il2CppSystem.Collections.Generic.List&lt;LlmMessage&gt; 能反射构造、能 Add
///   · 换成内容完全相同的列表后，对话 / TTS / 表情全部正常
///
/// 【失败保护】任何一步失败一律返回 null —— 调用方保持游戏原值，功能退化但不崩。
/// </summary>
public static class CanonStore
{
    private sealed class Entry
    {
        public object Msg;      // Il2Cpp LlmMessage 引用（持有即保 GC）
        public string Role;
        public string Content;
        public string Fp;       // role|hash(content)
    }

    private static readonly Dictionary<string, List<Entry>> _canon = new();
    private static readonly Dictionary<string, bool> _loaded = new();
    private static readonly object _gate = new();

    /// <summary>是否开启接管（实验期硬开，稳定后接 PromptConfig）</summary>
    public static bool Enabled = true;

    /// <summary>归档后保留多少轮（0 = 不裁剪）</summary>
    public static int KeepRounds = 20;

    /// <summary>
    /// 归档节拍：canon 涨到 (KeepRounds + ArchiveEvery) 轮时触发一次，
    /// 一次性裁掉最老 ArchiveEvery 轮。
    /// ★ 不能每轮裁 1 条 —— 那就变成滑动窗口，等于白干。
    /// ★ 2026-11-05 03:32 修正：历史 39 轮时模型输出格式崩了（97 字纯文本、无 opts/a1ttsv2），
    ///   所以保留轮数从 30 降到 20，节拍仍是 10（30 轮触发 → 裁到 20 轮）。
    /// </summary>
    public static int ArchiveEvery = 10;

    /// <summary>
    /// 是否丢掉 history 里的杂散 system 消息。
    /// ★ 2026-11-05：实测 canon 里混进 7 条「## 任务：闲聊」「你赠予了玩家一份心意x1」之类的
    ///   游戏状态提示，夹在对话中间打断节奏，是模型输出格式崩掉的元凶之一。
    ///   这些内容在动态 tail 里本来就有，历史里删掉不会丢信息。
    /// </summary>
    public static bool DropSystemInHistory = true;

    // 构造能力探测（启动时跑一次）
    private static Type _llmMsgType;
    private static ConstructorInfo _ctor0;
    private static PropertyInfo _pRole, _pContent;
    private static FieldInfo _fRole, _fContent;
    private static bool _ctorProbed;
    private static string _ctorNote = "未探测";

    /// <summary>当前 canon 有多少轮（日志/控制台用）</summary>
    public static int RoundsOf(string npcId)
    {
        lock (_gate) return _canon.TryGetValue(npcId, out var l) ? l.Count / 2 : 0;
    }

    /// <summary>用索引越界探测一个 history 对象的条数（诊断用）</summary>
    public static int CountItems(object hist)
    {
        if (hist == null) return -1;
        try
        {
            var m = hist.GetType().GetMethod("get_Item");
            if (m == null) return -1;
            for (int i = 0; i < 5000; i++)
            {
                try { m.Invoke(hist, new object[] { i }); }
                catch { return i; }
            }
            return 5000;
        }
        catch { return -2; }
    }

    /// <summary>
    /// 把任意 IReadOnlyList&lt;LlmMessage&gt; 转成 Il2CppSystem.Collections.Generic.List&lt;LlmMessage&gt;。
    /// ★ 游戏 Plan.History 的类型是 List&lt;T&gt;，直接塞 IReadOnlyList 会 ArgumentException。
    /// </summary>
    // ============================================================
    // ★ 2026-11-07（E 方案）：让最近 N 轮 assistant 在回写 history 时带原始 a1ttsv2
    //
    // 背景：游戏程序集 HistoryEnvelope.NormalizeOrWrap 会把 a1ttsv2/opts 剥掉再写历史，
    //      导致模型看到的 20 轮样例全是「没有台本」的负面示范（实测 22 条 0 条带 a1ttsv2）。
    //      唯一能拿到原始输出的入口是 Output.RawReplyStore（LogResponse Postfix）。
    // 做法：模型原始输出先按 content 前 40 字登记；回写 history 时，
    //      最近 UpgradeRounds 条 assistant 若命中，就换成原始版本（带 a1ttsv2）。
    // 代价：这些轮的历史变长（每轮 +500~800 字，5 轮约 +3,000~4,000 字）。
    // ============================================================

    /// <summary>
    /// 最近几轮 assistant 用原始输出替换（0=关闭）。
    /// ★ 2026-11-07 04:30 主人要求：**已禁用**——实测 34 轮掉 6 次 = 17.6%，
    ///   与之前 16~19% 无差异（5 条正面样例 vs 15 条负面，权重不够；且补样例又加长 prompt 两头抵消）。
    ///   改回 5 即可恢复（代码保留，机制本身是通的）。
    /// </summary>
    public static int UpgradeRounds = 0;

    private const int RawCacheMax = 40;
    private static readonly Dictionary<string, string> _rawByContent = new();

    /// <summary>登记本轮模型的原始输出（带 a1ttsv2/opts）。由 AddNpcChatMessagePatch 在 Clear 前调用。</summary>
    public static void RememberRawReply(string rawJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;
            var key = ContentKey(rawJson);
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                _rawByContent[key] = rawJson;
                while (_rawByContent.Count > RawCacheMax)
                {
                    string victim = null;
                    foreach (var k in _rawByContent.Keys) { victim = k; break; }
                    if (victim == null) break;
                    _rawByContent.Remove(victim);
                }
            }
        }
        catch { }
    }

    /// <summary>取 JSON 里 content 的前 40 字当匹配键（洗过版和原始版的 content 相同）。</summary>
    private static string ContentKey(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return null;
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return null;
            var el = node["content"];
            if (el == null) return null;
            string c;
            try { c = el.GetValue<string>(); } catch { c = el.ToString(); }
            if (string.IsNullOrWhiteSpace(c)) return null;
            c = c.Trim();
            return c.Length <= 40 ? c : c.Substring(0, 40);
        }
        catch { return null; }
    }

    /// <summary>把列表里最近的 assistant 消息换成带 a1ttsv2 的原始版本。</summary>
    private static List<object> UpgradeRecentAssistants(List<object> items)
    {
        try
        {
            if (items == null || items.Count == 0 || UpgradeRounds <= 0) return items;
            int replaced = 0;
            lock (_gate)
            {
                if (_rawByContent.Count == 0) return items;
                for (int i = items.Count - 1; i >= 0 && replaced < UpgradeRounds; i--)
                {
                    var role = GetMsgStr(items[i], _pRole, _fRole);
                    if (role != "assistant") continue;
                    var content = GetMsgStr(items[i], _pContent, _fContent);
                    var key = ContentKey(content);
                    if (key == null || !_rawByContent.TryGetValue(key, out var raw)) continue;
                    var msg = NewLlmMessage("assistant", raw);
                    if (msg == null) continue;
                    items[i] = msg;
                    replaced++;
                }
            }
            if (replaced > 0)
                MemoryEngine.Log?.Invoke($"[历史] E方案：{replaced} 轮 assistant 换成原始输出（带 a1ttsv2 台本）", false);
        }
        catch { }
        return items;
    }

    private static string GetMsgStr(object msg, PropertyInfo p, FieldInfo f)
    {
        try
        {
            if (msg == null) return null;
            if (p != null) return p.GetValue(msg) as string;
            if (f != null) return f.GetValue(msg) as string;
        }
        catch { }
        return null;
    }

    private static object NewLlmMessage(string role, string content)
    {
        try
        {
            if (_ctor0 == null || _llmMsgType == null) return null;
            var m = Activator.CreateInstance(_llmMsgType);
            if (m == null) return null;
            if (_pRole != null) _pRole.SetValue(m, role);
            else if (_fRole != null) _fRole.SetValue(m, role);
            else return null;
            if (_pContent != null) _pContent.SetValue(m, content);
            else if (_fContent != null) _fContent.SetValue(m, content);
            else return null;
            return m;
        }
        catch { return null; }
    }

    public static object ToTypedList(object src)
    {
        if (src == null) return null;
        try
        {
            var t = src.GetType();
            var itemM = t.GetMethod("get_Item");
            if (itemM == null) return null;

            var items = new List<object>();
            for (int i = 0; i < 5000; i++)
            {
                object it;
                try { it = itemM.Invoke(src, new object[] { i }); }
                catch { break; }
                if (it == null) break;
                items.Add(it);
            }
            if (items.Count == 0) return null;

            // ★ 2026-11-07（E 方案）：最近 N 轮 assistant 换成带 a1ttsv2 的原始版本
            items = UpgradeRecentAssistants(items);

            var elemType = items[0].GetType();
            var listType = typeof(Il2CppSystem.Collections.Generic.List<>).MakeGenericType(elemType);
            var list = Activator.CreateInstance(listType);
            var addM = listType.GetMethod("Add");
            if (list == null || addM == null) return null;
            foreach (var it in items) addM.Invoke(list, new object[] { it });
            return list;
        }
        catch { return null; }
    }

    public static void Reset()
    {
        lock (_gate) { _canon.Clear(); _loaded.Clear(); }
    }

    /// <summary>
    /// ★ 2026-11-11：估算一个 history 列表的实际文本字符数（读不到返回 -1）。
    /// 用途：请求大小探针（`Diagnostics.RequestProbe`）要算「history 到底占多少字」，
    /// 好判断玩家报的「上下文超出模型上限」是长度问题还是别的原因。
    /// </summary>
    public static int EstimateChars(object hist)
    {
        if (hist == null) return -1;
        try
        {
            var itemM = hist.GetType().GetMethod("get_Item");
            if (itemM == null) return -1;
            System.Reflection.MethodInfo contentM = null;
            int total = 0, n = 0;
            for (int i = 0; i < 5000; i++)
            {
                object it;
                try { it = itemM.Invoke(hist, new object[] { i }); }
                catch { break; }
                if (it == null) break;
                int len = 40;   // 读不到按 40 字/条保守估
                try
                {
                    contentM ??= it.GetType().GetMethod("get_Content");
                    if (contentM != null)
                    {
                        var v = contentM.Invoke(it, null);
                        var s = v?.ToString();
                        if (!string.IsNullOrEmpty(s)) len = s.Length;
                    }
                }
                catch { }
                total += len;
                n++;
            }
            return n == 0 ? -1 : total;
        }
        catch { return -1; }
    }

    /// <summary>
    /// 启动探测：能不能用反射造 LlmMessage + 无参构造 / 可写属性。
    /// 只写日志，不改任何东西。Plugin 启动时调一次即可。
    /// </summary>
    public static void ProbeCtorOnce()
    {
        if (_ctorProbed) return;
        _ctorProbed = true;
        var log = MemoryEngine.Log;
        void L(string m) => log?.Invoke(m, false);

        try
        {
            _llmMsgType = FindType("JNGame.Ainpc.Llm.LlmMessage");
            if (_llmMsgType == null) { _ctorNote = "找不到类型"; L("[历史接管] 探测：找不到 JNGame.Ainpc.Llm.LlmMessage 类型"); return; }

            L("[历史接管] 探测：类型=" + _llmMsgType.FullName);

            // 列构造
            var ctors = _llmMsgType.GetConstructors();
            var sb = new StringBuilder("[历史接管] 探测：构造函数 ").Append(ctors.Length).Append(" 个");
            foreach (var c in ctors)
            {
                sb.Append(" | (");
                var ps = c.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ps[i].ParameterType.Name);
                }
                sb.Append(')');
            }
            L(sb.ToString());

            // 找无参构造
            _ctor0 = _llmMsgType.GetConstructor(Type.EmptyTypes);
            _pRole = _llmMsgType.GetProperty("Role") ?? _llmMsgType.GetProperty("role");
            _pContent = _llmMsgType.GetProperty("Content") ?? _llmMsgType.GetProperty("content");
            _fRole = _llmMsgType.GetField("Role") ?? _llmMsgType.GetField("role");
            _fContent = _llmMsgType.GetField("Content") ?? _llmMsgType.GetField("content");

            L("[历史接管] 探测：无参构造=" + (_ctor0 != null)
              + " 角色属性=" + (_pRole != null || _fRole != null)
              + " 内容属性=" + (_pContent != null || _fContent != null));

            // 真的造一个试试
            if (_ctor0 != null)
            {
                try
                {
                    var one = Activator.CreateInstance(_llmMsgType);
                    _ctorNote = one != null ? "可构造 ✔" : "构造返回 null";
                    L("[历史接管] 探测：Activator 构造=" + _ctorNote);
                }
                catch (Exception e)
                {
                    _ctorNote = "构造抛异常 " + e.GetType().Name + ": " + e.Message;
                    L("[历史接管] 探测：" + _ctorNote);
                }
            }
            else
            {
                _ctorNote = "无无参构造，写不了字段 → 落盘无法恢复";
                L("[历史接管] 探测：" + _ctorNote);
            }
        }
        catch (Exception e)
        {
            _ctorNote = "探测异常 " + e.GetType().Name + ": " + e.Message;
            L("[历史接管] 探测异常：" + _ctorNote);
        }
    }

    /// <summary>当前构造能力描述（日志用）</summary>
    public static string CtorNote => _ctorNote;

    /// <summary>
    /// 尝试用 canon 替换游戏给的 history。
    /// 返回 null = 不接管（调用方必须保持原值）。
    /// </summary>
    public static object TryReplace(string npcId, object gameHistory, out string note)
    {
        note = null;
        if (!Enabled) { note = "接管开关关着"; return null; }
        if (string.IsNullOrEmpty(npcId)) { note = "npcId 为空"; return null; }
        if (gameHistory == null) { note = "gameHistory 为空"; return null; }

        lock (_gate)
        {
            try
            {
                EnsureLoaded(npcId);

                // ── 1. 读出游戏 history 全部条目 ──
                var items = ReadAll(gameHistory);
                if (items == null) { note = "读不到 get_Item"; return null; }
                if (items.Count == 0) { note = "游戏 history 为空"; return null; }

                // ── 2. 每条读 role/content，顺手丢掉杂散 system ──
                var roles = new List<string>(items.Count);
                var texts = new List<string>(items.Count);
                var kept = new List<object>(items.Count);
                int droppedSys = 0;
                foreach (var it in items)
                {
                    var role = ReadStr(it, "Role", "role");
                    var content = ReadStr(it, "Content", "content", "Text", "text");
                    if (role == null || content == null) { note = "读不到 Role/Content"; return null; }
                    if (DropSystemInHistory && role == "system") { droppedSys++; continue; }
                    roles.Add(role);
                    texts.Add(content);
                    kept.Add(it);
                }
                if (kept.Count == 0) { note = "过滤杂散 system 后为空"; return null; }

                // ── 3. 取 canon ──
                if (!_canon.TryGetValue(npcId, out var list))
                {
                    list = new List<Entry>();
                    _canon[npcId] = list;
                }

                // ── 4. 在游戏历史里「定位 canon 尾部」★★ 2026-11-11 修 ──
                //
                // 旧逻辑：假设「canon 尾部 = 游戏历史头部」，只在两边条数相同时碰巧成立。
                //   实测（2026-11-11 玩家日志，两份共 40+ 轮）：
                //     游戏给的条数会随玩家「上下文长度」设置一路涨到 60，
                //     而 canon 在 8 档只保留 8 轮 = 16 条
                //     → 游戏头部是【最老】的消息、canon 尾部是【最新】的 → 永远对不上
                //     → 重叠=0 → 每轮全量重建(★重建) + 每轮归档 44 条 + 每轮触发一次
                //       后台合并 LLM 调用 → 和主请求抢同一个 key → 空响应(rawChars=0)
                //       → 游戏显示「上下文超出模型上限」。聊得越久撞得越多。
                //
                // 新逻辑：拿 canon 的最后一条（最新消息）在游戏历史里【从尾部往前】定位，
                //   找到后往前验证最大连续匹配长度。这样游戏给 20 条还是 200 条都无所谓。
                int overlap = 0;
                int matchStart = -1;   // canon 的 k 条在游戏历史里的起始下标
                if (list.Count > 0 && kept.Count > 0)
                {
                    string lastFp = list[list.Count - 1].Fp;
                    int lastPos = -1;
                    int scanFrom = Math.Max(0, kept.Count - 64);   // 最多回溯 64 条（够覆盖本轮新增）
                    for (int i = kept.Count - 1; i >= scanFrom; i--)
                    {
                        if (Fingerprint(roles[i], texts[i]) == lastFp) { lastPos = i; break; }
                    }
                    if (lastPos >= 0)
                    {
                        int k = 0;
                        while (k < list.Count && k <= lastPos)
                        {
                            int ci = list.Count - 1 - k;
                            int gi = lastPos - k;
                            if (list[ci].Fp != Fingerprint(roles[gi], texts[gi])) break;
                            k++;
                        }
                        overlap = k;
                        matchStart = lastPos - k + 1;
                    }
                }

                bool rebuilt = false;
                if (overlap == 0 && list.Count > 0)
                {
                    list.Clear();
                    rebuilt = true;
                    matchStart = -1;
                }

                // ── 5. 追加新增 ──
                //   ★ 2026-11-11：只追加「匹配段之后」的消息。
                //   匹配段【之前】的老消息（游戏有、canon 已裁剪归档过的）不再重复塞回来
                //   —— 这正是「每轮归档 44 条 + 每轮合并」的源头。
                int added = 0;
                int appendFrom = (matchStart >= 0) ? (matchStart + overlap) : 0;
                if (appendFrom < 0) appendFrom = 0;
                if (appendFrom > kept.Count) appendFrom = kept.Count;
                for (int j = appendFrom; j < kept.Count; j++)
                {
                    list.Add(new Entry
                    {
                        Msg = kept[j],
                        Role = roles[j],
                        Content = texts[j],
                        Fp = Fingerprint(roles[j], texts[j])
                    });
                    added++;
                }

                // ── 6. 归档节拍：涨到「保留+节拍」轮时，一次性裁掉「节拍」轮 ──
                //    ★ 2026-11-05 修正（主人指出）：不能每轮裁 1 条，那就退化成滑动窗口，
                //      messages[2] 每轮照样变，缓存等于没救。
                //      正确：保留 20 轮 → 涨到 30 轮 → 一次裁 10 轮 → 回到 20 轮。
                //      裁掉的先落盘 archive_pending.jsonl（压缩总述是后续步骤，先不丢数据）。
                int dropped = 0;
                if (KeepRounds > 0 && ArchiveEvery > 0)
                {
                    int triggerItems = (KeepRounds + ArchiveEvery) * 2;   // 30 轮 = 60 条
                    int targetItems = KeepRounds * 2;                      // 20 轮 = 40 条
                    if (list.Count >= triggerItems)
                    {
                        int cutItems = list.Count - targetItems;
                        if (cutItems % 2 == 1) cutItems--;                 // 成对裁，不劈开 user/assistant
                        if (cutItems > 0)
                        {
                            var cut = list.GetRange(0, cutItems);
                            try { SavePendingArchive(npcId, cut); } catch { }
                            list.RemoveRange(0, cutItems);
                            dropped = cutItems;
                            // ★ 2026-11-07：裁剪与合并已**解耦**。
                            //   裁剪只负责「裁 + 落 pending」；合并按「合并节拍」累积触发（见 CheckMergeDue）。
                            //   原因：裁剪会断缓存（从 history 开头），合并也会断缓存（从 s0 记忆段，更贵），
                            //         绑在一起是「每裁一次就多一次最贵的断点」；解耦后合并次数少 2~5 倍。
                            try { CheckMergeDue(npcId, list.Count / 2); } catch { }
                        }
                    }
                }

                // ── 7. 落盘（有变化才写）──
                if (added > 0 || dropped > 0 || rebuilt)
                {
                    try { SaveFile(npcId); } catch { }
                }

                // ── 8. 构造新 List ──
                var merged = BuildList(list);
                if (merged == null) { note = "构造 List 失败"; return null; }

                note = $"canon={list.Count}条({list.Count / 2}轮)"
                     + $" 游戏={kept.Count}条"
                     + (droppedSys > 0 ? "(丢system " + droppedSys + ")" : "")
                     + $" 重叠={overlap}"
                     + (matchStart >= 0 ? "@" + matchStart : "")
                     + $" 新增={added}"
                     + (dropped > 0 ? " 裁=" + dropped : "")
                     + (rebuilt ? " ★重建" : "");
                return merged;
            }
            catch (Exception e)
            {
                note = "异常 " + e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// ★ 2026-11-07：裁剪与合并解耦后的「该不该合并」判断。
    /// 读 archive_pending.jsonl 累积条数（每轮 user+assistant = 2 条），
    /// 达到 CanonMergeEvery 轮才触发一次合并；合并成功后由 Extractor 清空 pending。
    /// </summary>
    private static void CheckMergeDue(string npcId, int remainRounds)
    {
        if (PromptConfig.CanonMergeEvery <= 0) return;
        var path = System.IO.Path.Combine(MemoryPaths.MemDir(npcId), "archive_pending.jsonl");
        if (!System.IO.File.Exists(path)) return;

        int lines = 0;
        try
        {
            foreach (var l in System.IO.File.ReadAllLines(path, Encoding.UTF8))
                if (!string.IsNullOrWhiteSpace(l)) lines++;
        }
        catch { return; }

        var pendingRounds = lines / 2;
        if (pendingRounds < PromptConfig.CanonMergeEvery)
        {
            MemoryEngine.Log?.Invoke(
                $"[合并] 待整合 {pendingRounds}/{PromptConfig.CanonMergeEvery} 轮（未到合并节拍）", false);
            return;
        }
        try
        {
            BuWenFanChen.Memory.Extractor.TriggerArchiveMerge(npcId, pendingRounds, remainRounds);
        }
        catch { }
    }

    // ───────── 落盘 ─────────

    private static string FileOf(string npcId) =>
        System.IO.Path.Combine(MemoryPaths.MemDir(npcId), "canon.jsonl");

    /// <summary>首次用到该角色时，从文件恢复 canon（需要的对象能构造才真恢复）</summary>
    private static void EnsureLoaded(string npcId)
    {
        if (_loaded.TryGetValue(npcId, out var done) && done) return;
        _loaded[npcId] = true;

        var log = MemoryEngine.Log;
        var path = FileOf(npcId);
        if (!System.IO.File.Exists(path)) return;

        try
        {
            ProbeCtorOnce();
            if (_ctor0 == null)
            {
                log?.Invoke("[历史接管] 有 canon 文件但无法构造 LlmMessage → 不恢复，从游戏当前窗口重建", false);
                return;
            }

            var list = new List<Entry>();
            foreach (var line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var role = line.Substring(0, tab);
                var b64 = line.Substring(tab + 1);
                if (DropSystemInHistory && role == "system") continue;   // 丢掉落盘里的杂散 system
                string content;
                try { content = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                catch { continue; }

                var obj = Activator.CreateInstance(_llmMsgType);
                if (obj == null) { continue; }
                if (!WriteStr(obj, role, content)) { continue; }

                list.Add(new Entry
                {
                    Msg = obj,
                    Role = role,
                    Content = content,
                    Fp = Fingerprint(role, content)
                });
            }

            if (list.Count > 0)
            {
                _canon[npcId] = list;
                log?.Invoke($"[历史接管] 从文件恢复 canon：{list.Count} 条（{list.Count / 2} 轮）", false);
            }
        }
        catch (Exception e)
        {
            log?.Invoke("[历史接管] 恢复 canon 失败：" + e.GetType().Name + " " + e.Message, false);
        }
    }

    private static void SaveFile(string npcId)
    {
        if (!_canon.TryGetValue(npcId, out var list)) return;
        var path = FileOf(npcId);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

        var sb = new StringBuilder(list.Count * 200);
        foreach (var e in list)
        {
            sb.Append(e.Role).Append('\t')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Content)))
              .Append('\n');
        }
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 把即将被裁掉的轮次追加进 archive_pending.jsonl，并返回可读文本供「归档即合并」使用。
    /// ★ 目的：合并是异步/可失败的，先落盘保证「裁了也丢不了」；
    ///   原文本身也在 chunks.jsonl 里（每轮写入），最坏情况还能靠检索找回。
    /// ★ 2026-11-07：返回值喂给 Extractor.TriggerArchiveMerge()。
    /// </summary>
    private static string SavePendingArchive(string npcId, List<Entry> cut)
    {
        if (cut == null || cut.Count == 0) return null;
        var path = System.IO.Path.Combine(MemoryPaths.MemDir(npcId), "archive_pending.jsonl");
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

        var sb = new StringBuilder(cut.Count * 200);
        var plain = new StringBuilder(cut.Count * 400);
        foreach (var e in cut)
        {
            sb.Append(e.Role).Append('\t')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Content)))
              .Append('\n');
            plain.Append(e.Role == "user" ? "玩家：" : "她：").AppendLine(e.Content ?? "");
        }
        System.IO.File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        MemoryEngine.Log?.Invoke($"[历史] 归档待处理：{cut.Count} 条入 archive_pending.jsonl", false);
        return plain.ToString();
    }

    // ───────── 工具 ─────────

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static List<object> ReadAll(object hist)
    {
        var t = hist.GetType();
        var itemM = t.GetMethod("get_Item");
        if (itemM == null) return null;

        var list = new List<object>();
        for (int i = 0; i < 2000; i++)
        {
            object it;
            try { it = itemM.Invoke(hist, new object[] { i }); }
            catch { break; }
            if (it == null) break;
            list.Add(it);
        }
        return list;
    }

    private static object BuildList(List<Entry> entries)
    {
        if (entries.Count == 0) return null;
        var elemType = entries[0].Msg.GetType();
        var listType = typeof(Il2CppSystem.Collections.Generic.List<>).MakeGenericType(elemType);
        var list = Activator.CreateInstance(listType);
        var addM = listType.GetMethod("Add");
        if (list == null || addM == null) return null;
        foreach (var e in entries) addM.Invoke(list, new object[] { e.Msg });
        return list;
    }

    private static string ReadStr(object obj, params string[] names)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var n in names)
        {
            try
            {
                var p = t.GetProperty(n);
                if (p != null)
                {
                    var v = p.GetValue(obj);
                    if (v != null) return v.ToString();
                    continue;
                }
                var f = t.GetField(n);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v != null) return v.ToString();
                }
            }
            catch { }
        }
        return null;
    }

    private static bool WriteStr(object obj, string role, string content)
    {
        try
        {
            if (_pRole != null) _pRole.SetValue(obj, role);
            else if (_fRole != null) _fRole.SetValue(obj, role);
            else return false;

            if (_pContent != null) _pContent.SetValue(obj, content);
            else if (_fContent != null) _fContent.SetValue(obj, content);
            else return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 消息指纹：role|hash(归一化正文)。
    ///
    /// ★ 2026-11-11 加归一化（玩家报「聊一小时就超上下文」的根因之一）：
    ///   同一条 assistant 消息在两边长得不一样——
    ///     · 我们落盘/接管时拿到的是【模型原始输出】带 {"content":..., "a1ttsv2":..., "opts":...}
    ///     · 游戏还回来的 history 是【TTS MOD 洗过】的纯文本
    ///   旧实现直接 hash 原文 → 同一条消息永远判成两条 → 重叠=0。
    ///   实测日志：canon=15/游戏=15 时重叠只有 13（漏配 2 条）；条数一不等直接 0。
    ///   现在先剥 JSON 外壳取正文，再删掉全部空白，两边就能对上。
    /// </summary>
    private static string Fingerprint(string role, string content) => role + "|" + Fnv(NormalizeForFp(content));

    /// <summary>
    /// 指纹用归一化：像 JSON 就取正文，然后去掉所有空白字符。
    /// 只影响比对，不改动任何真正发出去的内容。
    /// </summary>
    private static string NormalizeForFp(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var s = content.Trim();
        if (s.Length == 0) return "";

        // ① 剥 JSON 外壳（我们存的原始输出是 {"content":..,"a1ttsv2":..,"opts":..}）
        if (s[0] == '{' || s[0] == '[')
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(s);
                if (node is System.Text.Json.Nodes.JsonObject o)
                {
                    var c = o["content"];
                    if (c != null)
                    {
                        string str = null;
                        try { str = c.GetValue<string>(); } catch { }
                        if (string.IsNullOrEmpty(str))
                        {
                            try { str = c.ToString()?.Trim('"'); } catch { }
                        }
                        if (!string.IsNullOrEmpty(str)) s = str;
                    }
                }
                else if (node is System.Text.Json.Nodes.JsonArray arr)
                {
                    var sb = new StringBuilder();
                    foreach (var it in arr)
                    {
                        try
                        {
                            var v = it is System.Text.Json.Nodes.JsonObject jo ? jo["content"] : it;
                            if (v != null) sb.Append(v.ToString());
                        }
                        catch { }
                    }
                    if (sb.Length > 0) s = sb.ToString();
                }
            }
            catch { }
        }

        // ② 去掉全部空白（换行/缩进差异不该影响"是不是同一条"）
        bool hasWs = false;
        foreach (var ch in s) { if (char.IsWhiteSpace(ch)) { hasWs = true; break; } }
        if (!hasWs) return s;

        var outSb = new StringBuilder(s.Length);
        foreach (var ch in s) if (!char.IsWhiteSpace(ch)) outSb.Append(ch);
        return outSb.ToString();
    }

    /// <summary>FNV-1a，跨进程稳定（不能用 string.GetHashCode，.NET 会随机化种子）</summary>
    private static string Fnv(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h.ToString("x8");
        }
    }
}
