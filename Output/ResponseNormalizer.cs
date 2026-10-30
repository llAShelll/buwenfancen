using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BuWenFanChen.Output;

/// <summary>
/// 模型输出的「容错解析 + 字数切分 + 语音帧保护」。
/// 设计原则：结构类格式由程序保证，语义类交给模型。
/// 本类不依赖 Unity / IL2CPP，可独立单元测试。
/// </summary>
internal static class ResponseNormalizer
{
    // ============================================================
    // ① 七种输出形态兼容解析
    // ============================================================
    /// <summary>
    /// 模型偶尔把 &lt;a1tts_v2&gt;{...}&lt;/a1tts_v2&gt; 整段塞进 content 字符串里，且内层双引号没转义。
    /// 这种输出整段不是合法 JSON：花括号扫描会被内层对象带偏，最终 content 丢失、界面落到固定兜底文案
    /// （表现为「连续几轮重复同一句」）。
    /// 处理：把这些帧原样剥出来，主体就能正常解析；帧本身单独解析后补成 a1ttsv2。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex InlineTtsFrameRe = new(
        @"<a1tts[_-]?v2>\s*(\{.*?\})\s*</a1tts[_-]?v2>",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static JsonObject ParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();

        // 形态 0：content 内嵌 a1tts_v2 帧（内层引号未转义）→ 先剥帧再解析
        var frameMatch = InlineTtsFrameRe.Match(t);
        if (frameMatch.Success)
        {
            var frameRaw = frameMatch.Groups[1].Value;
            var stripped = t.Remove(frameMatch.Index, frameMatch.Length);
            if (TryParse(stripped, out var strippedObj))
            {
                try
                {
                    var fj = JsonNode.Parse(frameRaw) as JsonObject;
                    if (fj != null && !strippedObj.ContainsKey("a1ttsv2") && !strippedObj.ContainsKey("A1ttsv2"))
                        strippedObj["a1ttsv2"] = fj;
                }
                catch { }
                return strippedObj;
            }
        }

        // 形态 1：完美 JSON
        if (TryParse(t, out var obj)) return obj;

        // 形态 2：Markdown 代码块
        var noFence = StripFence(t);
        if (noFence != t && TryParse(noFence, out obj)) return obj;

        // 形态 3：前后有解释文字 → 抠括号
        var cand = ExtractOutermostObject(noFence);
        if (cand == null) return null;
        if (TryParse(cand, out obj)) return obj;

        // 形态 4：键没引号
        var fixedBare = QuoteBareKeys(cand);
        if (TryParse(fixedBare, out obj)) return obj;

        // 形态 5：中文引号
        var fixedQuote = QuoteBareKeys(NormalizeQuotes(cand));
        if (TryParse(fixedQuote, out obj)) return obj;

        // 形态 6：尾逗号 / 单引号
        var fixedTail = RemoveTrailingCommas(fixedQuote.Replace('\'', '"'));
        if (TryParse(fixedTail, out obj)) return obj;

        // 形态 7：纯文本 → 返回 null，由调用方当 content 处理
        return null;
    }

    /// <summary>纯文本兜底：把整段非 JSON 输出当 content。</summary>
    public static string AsPlainContent(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = StripFence(raw.Trim());
        var cand = ExtractOutermostObject(t);
        return (cand != null ? t.Replace(cand, "") : t).Trim();
    }

    private static bool TryParse(string s, out JsonObject obj)
    {
        obj = null;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            if (JsonNode.Parse(s) is JsonObject o)
            {
                obj = o;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var body = s[(firstNl + 1)..];
        var end = body.LastIndexOf("```", StringComparison.Ordinal);
        return end >= 0 ? body[..end].Trim() : body.Trim();
    }

    /// <summary>找最外层平衡的 {...}，正确处理字符串内的括号与转义。</summary>
    private static string ExtractOutermostObject(string s)
    {
        var start = s.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (--depth == 0) return s[start..(i + 1)];
            }
        }
        return null;
    }

    private static readonly Regex BareKey = new("([{,]\\s*)([A-Za-z_][A-Za-z0-9_]*)(\\s*:)", RegexOptions.Compiled);

    /// <summary>给没引号的键补引号：{content: "..."} → {"content": "..."}（只在字符串外生效）。</summary>
    private static string QuoteBareKeys(string s)
    {
        if (s == null) return null;
        var sb = new System.Text.StringBuilder(s.Length + 32);
        var inStr = false;
        var esc = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                sb.Append(c);
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }

            // 可能是裸键的起点
            if ((c == '{' || c == ',') && TryMatchBareKey(s, i, out var replaced, out var consumed))
            {
                sb.Append(replaced);
                i += consumed - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryMatchBareKey(string s, int at, out string replaced, out int consumed)
    {
        replaced = null;
        consumed = 0;
        var m = BareKey.Match(s, at);
        if (!m.Success || m.Index != at) return false;
        replaced = m.Groups[1].Value + "\"" + m.Groups[2].Value + "\"" + m.Groups[3].Value;
        consumed = m.Length;
        return true;
    }

    private static string NormalizeQuotes(string s)
    {
        if (s == null) return null;
        // 只把作为 JSON 分隔符的中文引号换掉：简化为全量替换（结构里极少数情况会误伤中文内容里的引号）
        return s.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');
    }

    private static string RemoveTrailingCommas(string s)
    {
        if (s == null) return null;
        return Regex.Replace(s, @",\s*([}\]])", "$1");
    }

    // ============================================================
    // ② 语音帧保护 + 空行折叠 + 字数切分
    // ============================================================
    /// <summary>
    /// 折叠空行（结构类，程序保证）。
    /// · 模型常写「双换行」分段 → 游戏 UI 渲染成空段 ✗
    /// · 这里把连续换行压成单个：旁白/台词仍分行，但不再留空行 ✓
    /// · 顺带处理模型双层转义写出来的字面量 \n（两个字符）。
    /// </summary>
    public static string CollapseBlankLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // ① 字面量转义 → 真换行（模型偶尔把 \n 当文字写出来）
        if (text.Contains("\\n") || text.Contains("\\r"))
            text = text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");

        // ② 换行归一
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // ③ 连续换行 → 单个（while 循环，三连也能压掉）
        while (text.Contains("\n\n"))
            text = text.Replace("\n\n", "\n");

        // ④ 每行去首尾空白，整体 trim
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim();
        return string.Join("\n", lines).Trim();
    }

    private static readonly Regex FrameRe =
        new(@"<\s*a1tts?_v1\s*>.*?(?:<\s*/\s*a1tts?_v1\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>把 content 拆成「纯文本」和「语音帧」。帧会被拼回末尾。</summary>
    public static (string Text, string Frame) SplitFrame(string content)
    {
        if (string.IsNullOrEmpty(content)) return ("", null);
        var m = FrameRe.Match(content);
        if (!m.Success) return (content, null);
        var text = content.Remove(m.Index, m.Length).Trim();
        return (text, m.Value);
    }

    public static string ClipContent(string content, int maxChars, bool keepFrame = true)
    {
        if (string.IsNullOrEmpty(content) || maxChars <= 0) return content;
        var (text, frame) = SplitFrame(content);
        if (CountVisible(text) <= maxChars) return content;
        var clipped = ClipAtBoundary(text, maxChars);
        return keepFrame && frame != null ? clipped + frame : clipped;
    }

    /// <summary>可见字数：跳过 <a1tts_v1> 帧与空白。</summary>
    public static int CountVisible(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var (text, _) = SplitFrame(s);
        var n = 0;
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    /// <summary>
    /// 按句界切到 limit 以内。
    /// 优先级：。！？ → ，、； → 空格。
    /// ★ 绝不切在（）内、引号内、转义序列中：如果 limit 落在这些结构里，
    ///   就往后找到结构闭合后的第一个句界（宁可变长也不破坏结构）；
    ///   整段都没有闭合则【不切】。
    /// </summary>
    public static string ClipAtBoundary(string text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cut = FindCut(text, limit);
        return cut > 0 ? text[..cut].TrimEnd() : text;
    }

    /// <summary>返回切点（不含）；0 表示不该切。</summary>
    private static int FindCut(string text, int limit)
    {
        var visible = 0;
        var depth = 0;
        var inQuote = false;
        var cutPeriod = -1;
        var cutComma = -1;
        var cutSpace = -1;
        var cutAfter = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\') { i++; visible++; continue; }
            if (char.IsWhiteSpace(c)) continue;

            if (c is '（' or '(') depth++;
            else if (c is '）' or ')') depth = Math.Max(0, depth - 1);
            else if (c is '"' or '“' or '”') inQuote = !inQuote;

            visible++;

            // 只在「括号外 + 引号外」才算合法切点
            if (depth > 0 || inQuote) continue;

            var isPeriod = c is '。' or '！' or '？' or '.' or '!' or '?';
            var isComma = c is '，' or '、' or '；' or ',' or ';';
            var isSpace = c == ' ' || c == '\n';

            if (visible <= limit)
            {
                if (isPeriod) cutPeriod = i + 1;
                else if (isComma) cutComma = i + 1;
                else if (isSpace) cutSpace = i + 1;
            }
            else if (cutAfter < 0 && (isPeriod || isComma || isSpace))
            {
                cutAfter = i + 1;   // limit 之后第一个合法切点
            }

            if (visible > limit && cutAfter > 0 && cutPeriod <= 0 && cutComma <= 0 && cutSpace <= 0)
                break;
        }

        if (cutPeriod > 0) return cutPeriod;
        if (cutComma > 0) return cutComma;
        if (cutSpace > 0) return cutSpace;
        return cutAfter;   // 可能为 -1（不切）
    }

    // ============================================================
    // ③ 旁白 / 台词分别计数（供字数上限分档用）
    // ============================================================
    /// <summary>统计（）内的旁白字数。</summary>
    public static int CountNarration(string text)
    {
        var n = 0;
        var depth = 0;
        foreach (var c in text ?? "")
        {
            if (c is '（' or '(') { depth++; continue; }
            if (c is '）' or ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth > 0 && !char.IsWhiteSpace(c)) n++;
        }
        return n;
    }

    /// <summary>统计""内的台词字数。</summary>
    public static int CountDialogue(string text)
    {
        var n = 0;
        var inQ = false;
        foreach (var c in text ?? "")
        {
            if (c is '"' or '“') { inQ = true; continue; }
            if (c is '”') { inQ = false; continue; }
            if (inQ && !char.IsWhiteSpace(c)) n++;
        }
        return n;
    }

    // ============================================================
    // ④ 按通道硬上限（总 / 旁白 / 台词）
    // ============================================================
    private enum SegKind { Narration, Dialogue }

    private sealed class Seg
    {
        public SegKind Kind;
        public string Text;
    }

    /// <summary>
    /// 把 content 解析成「段」：（）包起来的是旁白，""包起来的是台词，
    /// 没有包裹的裸文本也算旁白（它同样是叙述）。
    /// </summary>
    private static List<Seg> ParseSegments(string text)
    {
        var segs = new List<Seg>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '（' or '(')
            {
                // ★ 2026-10-24 16:40 修复（外部审计 F3）：
                //   原来用 IndexOf(close) 只找第一个闭括号，遇到嵌套
                //   （外层（内层）旁白）会在内层的「）」处截断，把里面的引号内容误判成对白。
                //   现在用深度计数，成对匹配、可嵌套，全角/半角混用也配平。
                var depth = 0;
                var j = i;
                for (; j < text.Length; j++)
                {
                    var ch = text[j];
                    if (ch is '（' or '(') depth++;
                    else if (ch is '）' or ')')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }
                if (j >= text.Length)
                {
                    // 括号没闭合 → 剩余整体当旁白，结束
                    segs.Add(new Seg { Kind = SegKind.Narration, Text = text[i..] });
                    break;
                }
                segs.Add(new Seg { Kind = SegKind.Narration, Text = text[i..(j + 1)] });
                i = j + 1;
                continue;
            }
            if (c is '"' or '“' or '”')
            {
                // 找配对的结束引号
                var startQuote = c;
                var k = i + 1;
                while (k < text.Length)
                {
                    if (text[k] == '"' || text[k] == '”') break;
                    if (startQuote == '“' && text[k] == '“') break;
                    k++;
                }
                if (k < text.Length)
                {
                    segs.Add(new Seg { Kind = SegKind.Dialogue, Text = text[i..(k + 1)] });
                    i = k + 1;
                    continue;
                }
                segs.Add(new Seg { Kind = SegKind.Dialogue, Text = text[i..] });
                break;
            }
            // 裸文本：吃到下一个（）或引号之前
            var m = i;
            while (m < text.Length && text[m] is not ('（' or '(' or '"' or '“'))
                m++;
            if (m == i) m = i + 1;
            var raw = text[i..m];
            segs.Add(new Seg { Kind = SegKind.Narration, Text = raw });
            i = m;
        }
        return segs;
    }

    /// <summary>
    /// 某段的有效字数。
    /// ★ 必须和 CountNarration / CountDialogue 同口径：
    ///   旁白段数（）内的字，台词段数""内的字 —— 不含括号/引号本身。
    /// </summary>
    private static int SegChars(Seg sg)
        => sg.Kind == SegKind.Dialogue ? CountDialogue(sg.Text) : CountNarration(sg.Text);

    /// <summary>
    /// 按通道裁段。
    /// ★ C 方案（用户 2026-10-22 定）：
    ///   平时超限【只警告不删】；只有超过 hardRatio 倍（默认 1.5）才动刀。
    ///   动刀时从最长的同类段开始整段删除，绝不切半句/半个括号。
    /// 返回 (新文本, 删掉的旁白段数, 删掉的台词段数)
    /// </summary>
    public static (string Text, int DropNarr, int DropDial) EnforceChannelLimits(
        string text, int narrationLimit, int dialogueLimit, double hardRatio = 1.5,
        bool cutDialogue = true)
    {
        if (string.IsNullOrEmpty(text)) return (text, 0, 0);
        if (narrationLimit <= 0 && dialogueLimit <= 0) return (text, 0, 0);
        if (hardRatio < 1.0) hardRatio = 1.0;

        var segs = ParseSegments(text);
        var dropNarr = 0;
        var dropDial = 0;

        // 只有超过「上限 × hardRatio」才进入硬删循环
        var narrHard = (int)Math.Ceiling(narrationLimit * hardRatio);
        var dialHard = (int)Math.Ceiling(dialogueLimit * hardRatio);

        while (true)
        {
            var narr = segs.Where(x => x.Kind == SegKind.Narration).Sum(x => SegChars(x));
            if (narr <= narrHard || narrationLimit <= 0) break;
            // 找最长的旁白段删掉
            Seg longest = null;
            var max = -1;
            foreach (var sg in segs)
            {
                if (sg.Kind != SegKind.Narration) continue;
                var n = SegChars(sg);
                if (n > max) { max = n; longest = sg; }
            }
            if (longest == null) break;
            segs.Remove(longest);
            dropNarr++;
        }

        // ★ 2026-10-24 17:10 修复（display_spoken_content_changed）：
        //   台词是语音 MOD 要朗读的内容，删掉必然和它缓存的原始 content 对不上，
        //   它会直接判「显示内容变了」而拒绝朗读（实测三轮里两轮都这样哑掉）。
        //   所以台词【一律不裁】，只靠提示词控制长度；超了只记日志。
        while (cutDialogue)
        {
            var dial = segs.Where(x => x.Kind == SegKind.Dialogue).Sum(x => SegChars(x));
            if (dial <= dialHard || dialogueLimit <= 0) break;
            Seg longest = null;
            var max = -1;
            foreach (var sg in segs)
            {
                if (sg.Kind != SegKind.Dialogue) continue;
                var n = SegChars(sg);
                if (n > max) { max = n; longest = sg; }
            }
            if (longest == null) break;
            segs.Remove(longest);
            dropDial++;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var sg in segs) sb.Append(sg.Text);
        var result = CollapseBlankLines(sb.ToString());
        return (result, dropNarr, dropDial);
    }


    // ============================================================
    // ⑤ a1ttsv2 生成 + 契约校验（给语音 MOD 用，不依赖模型吐字段）
    // ============================================================

    /// <summary>a1ttsv2 字段在模型原始 JSON 里的形态。</summary>
    public enum A1TtsV2State
    {
        Absent,          // 字段不存在
        ExplicitNull,    // 显式 null（schema 允许，表示本轮不说话）
        Valid,           // 通过严格契约校验
        StringForm,      // 字符串化 JSON，且解出来通过校验
        Invalid,         // 存在但不符合契约
        AliasConflict,   // 同时存在 a1ttsv2 和 A1ttsv2
    }

    /// <summary>
    /// 严格校验 a1ttsv2 是否符合 TTS 消费契约。
    ///
    /// 规则来源：官方 a1ttsv2.schema.json + 2026-10-23 外审报告 F3 的 8 个反例。
    ///   · 额外字段（顶层或 segment 内）一律忽略不判错（Havoc 2026-10-23 确认 parser 接受）
    ///   · instruct：字符串，1~120 字
    ///   · segments：数组，1~64 条
    ///   · 每条：对象，只允许 text、tag
    ///   · text：字符串，1~4096 字
    ///   · tag：字符串，0~6 字（允许空）
    ///   · 过滤纯旁白之后必须仍有可朗读内容
    /// </summary>
    public static bool IsValidA1TtsV2(System.Text.Json.Nodes.JsonNode node, out string reason,
                                        int rawScriptLength = -1)
    {
        reason = null;
        if (node is not System.Text.Json.Nodes.JsonObject v2)
        {
            reason = "不是对象";
            return false;
        }

        // ★★ 2026-10-24 17:45 按消费端规则重写（A1TTSV2-CONSUMER-RULES 第 2、3 节）
        //
        //   和原来最大的差别：
        //   ① a1ttsv2 / segment 的【多余字段】消费端直接拒绝，不会帮忙剥离
        //      （原注释写「Havoc 确认接受额外字段」是错的，规则原文：
        //        invalid / a1ttsv2_unexpected_or_missing_fields）
        //   ② instruct 缺失/null/空/纯空白 → 消费端补中性值，算【可救】，不是 invalid
        //   ③ tag 不合规（长度、非中文区）→ 消费端【清空 tag 但保留台词】，也算可救
        //   ④ tag 是非字符串且非 null → 结构错误，整份 invalid
        //   ⑤ text 长度只查上界 4096（空字符串结构合法，只是过滤后会被丢弃）
        //
        //   这个方法回答的是：「这份台本交给消费端，能不能拿到可用台词？」

        // ① 顶层只允许 instruct / segments
        foreach (var kv in v2)
        {
            if (kv.Key != "instruct" && kv.Key != "segments")
            {
                reason = $"a1ttsv2 有多余字段「{kv.Key}」（消费端拒绝未知字段）";
                return false;
            }
        }

        // ② instruct：缺失 / null / 空 / 纯空白 都算可救（消费端补中性值）
        if (v2.TryGetPropertyValue("instruct", out var ins) && ins is not null)
        {
            if (ins is not System.Text.Json.Nodes.JsonValue insV || !insV.TryGetValue<string>(out var insStr))
            {
                reason = "instruct 不是字符串（消费端拒绝整份）";
                return false;
            }
            var insTrim = insStr.Trim();
            if (insTrim.Length > 120)
            {
                reason = $"instruct Trim 后 {insTrim.Length} 字，越界(≤120，消费端不截断)";
                return false;
            }
            // 空 / 纯空白 → 可救，继续
        }

        // ③ segments
        //    ★ 2026-10-24 17:55（官方用例 single_segment_object）：
        //      消费端支持「单个片段对象」→ 包成一元素数组再校验（repairChanges: wrap_single_segment）。
        //      我们也照样接受，避免把能用的台本判成非法而重建洗数据。
        if (!v2.TryGetPropertyValue("segments", out var segsNode) || segsNode is null)
        {
            reason = "segments 缺失";
            return false;
        }
        System.Text.Json.Nodes.JsonArray arr;
        if (segsNode is System.Text.Json.Nodes.JsonArray segArr)
        {
            arr = segArr;
        }
        else if (segsNode is System.Text.Json.Nodes.JsonObject singleSeg)
        {
            // JsonNode 没有 DeepClone，用「序列化再解析」拿到一份独立副本
            arr = new System.Text.Json.Nodes.JsonArray(
                System.Text.Json.Nodes.JsonNode.Parse(singleSeg.ToJsonString()));
        }
        else
        {
            reason = "segments 不是数组也不是对象";
            return false;
        }
        if (arr.Count < 1 || arr.Count > 64)
        {
            reason = $"segments 原始条数 {arr.Count} 越界(1~64，过滤前)";
            return false;
        }

        // ★ 16384 预算（对齐消费端「先修复再 ParseValue」的调用顺序，第 5 节）
        //   实测规律（官方用例）：
        //     原始超长 + tag 齐全    → 不触发外层修复 → 直接用原始长度 → 拒
        //     原始超长 + 缺 tag      → 触发修复、重序列化变短 → 通过
        if (rawScriptLength > 16384)
        {
            var needsRepair = false;
            if (!v2.ContainsKey("instruct") || v2["instruct"] is null) needsRepair = true;
            foreach (var it in arr)
            {
                if (it is not System.Text.Json.Nodes.JsonObject sg0) continue;
                if (!sg0.TryGetPropertyValue("tag", out var tg0) || tg0 is null) { needsRepair = true; break; }
            }
            if (!needsRepair)
            {
                reason = $"a1ttsv2 原始 {rawScriptLength} 字符超 16384，且输入无需修复（消费端直接按原长拒绝）";
                return false;
            }
            var compactLen = v2.ToJsonString().Length;
            if (compactLen > 16384)
            {
                reason = $"a1ttsv2 修复后仍 {compactLen} 字符，超 16384";
                return false;
            }
        }

        var speakable = 0;
        foreach (var item in arr)
        {
            if (item is not System.Text.Json.Nodes.JsonObject so)
            {
                reason = "存在非对象 segment";
                return false;
            }

            // segment 只允许 text / tag
            foreach (var kv in so)
            {
                if (kv.Key != "text" && kv.Key != "tag")
                {
                    reason = $"segment 有多余字段「{kv.Key}」（消费端拒绝未知字段）";
                    return false;
                }
            }

            if (!so.TryGetPropertyValue("text", out var tv) || tv is null)
            {
                reason = "segment.text 缺失（消费端不补成对白）";
                return false;
            }
            if (tv is not System.Text.Json.Nodes.JsonValue tval || !tval.TryGetValue<string>(out var txt))
            {
                reason = "segment.text 不是字符串";
                return false;
            }
            if (txt.Length > 4096)
            {
                reason = $"segment.text {txt.Length} 字，越界(≤4096，过滤前)";
                return false;
            }

            // tag：缺失/null 可救；字符串可救（不合规会被清空但保留台词）；其他类型拒绝
            if (so.TryGetPropertyValue("tag", out var tagNode) && tagNode is not null)
            {
                if (tagNode is not System.Text.Json.Nodes.JsonValue tagV || !tagV.TryGetValue<string>(out _))
                {
                    reason = "segment.tag 不是字符串（消费端拒绝整份）";
                    return false;
                }
            }

            if (!IsPureNarration(txt)) speakable++;
        }

        if (speakable == 0)
        {
            reason = "过滤旁白后无可朗读内容（消费端 segments_empty_after_filter）";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 按语音 MOD 的准确算法过滤括号旁白，返回实际可朗读的文本。
    ///
    /// ★★ 2026-10-24 17:45（对齐 A1TTSV2-CONSUMER-RULES 第 4 节）：
    ///   原来的实现是「整段是否被一对全角括号包住」，和消费端完全不同。
    ///   消费端是【逐字符深度扫描】，全角半角共用同一个计数器，规则：
    ///     1. 遇 ( 或 （ → 深度 +1；最外层开始时记住位置
    ///     2. 遇 ) 或 ）且深度 &gt; 0 → 深度 -1；深度回 0 时整组连括号一起过滤
    ///     3. 只把深度为 0 的普通字符写入结果
    ///     4. 扫描结束仍有未闭合括号 → 把最外层未闭合位置到结尾【原样补回】
    ///     5. 无对应左括号的右括号 → 原样保留
    ///     6. 结果里所有 \s+ 折成一个 ASCII 空格，再 Trim
    ///
    /// 对照（消费端实测）：
    ///     (点头)                        → 空
    ///     （外层（内层）旁白“假的台词”）  → 空（不把里面引号当对白）
    ///     甲（点头）乙                   → 甲乙
    ///     甲（动作)乙                    → 甲乙
    ///     甲（没有闭合                   → 原样保留
    ///     甲）乙                        → 原样保留
    ///     “你好。”                      → 引号也保留
    ///     [动作]你好                    → 原样保留（方括号不是旁白符号）
    /// </summary>
    public static string FilterNarration(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        var outermostStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '(' or '（')
            {
                if (depth == 0) outermostStart = i;
                depth++;
            }
            else if (ch is ')' or '）')
            {
                if (depth > 0)
                {
                    depth--;
                    if (depth == 0) outermostStart = -1;
                }
                else
                {
                    sb.Append(ch);   // 无对应左括号的右括号 → 保留
                }
            }
            else if (depth == 0)
            {
                sb.Append(ch);
            }
        }
        // 未闭合：把最外层未闭合位置到原文结尾原样补回
        if (depth > 0 && outermostStart >= 0 && outermostStart < text.Length)
            sb.Append(text[outermostStart..]);

        var result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
        return result.Trim();
    }

    /// <summary>
    /// 这段文本过滤旁白后是否没有任何可朗读内容（消费端 segments_empty_after_filter 的等价判定）。
    /// ★ 必须用 FilterNarration 的真实结果长度判断，不能只看"是否整段被一对括号包住"。
    /// </summary>
    public static bool IsPureNarration(string text) => FilterNarration(text).Length == 0;

    /// <summary>
    /// 从【原始回复字符串】里量出 a1ttsv2 值的原始文本长度。
    ///
    /// ★★ 2026-10-24 18:00（对齐 A1TTSV2-CONSUMER-RULES 第 5 节）：
    ///   消费端的长度预算是「进入 ParseValue 的那份 a1ttsv2 JSON 值文本」≤ 16384，
    ///   包含内部空白、键、引号、转义 —— 不是 segments.text 拼接长度。
    ///   所以必须量原始文本，不能用 ToJsonString() 重序列化（会把空白吃掉）。
    ///
    /// 返回 -1 表示找不到 a1ttsv2、或者它的值不是对象/数组（null / 字符串等）。
    /// </summary>
    public static int MeasureA1TtsV2ValueLength(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return -1;
        var idx = raw.IndexOf("\"a1ttsv2\"", StringComparison.Ordinal);
        if (idx < 0) return -1;
        var i = idx + "\"a1ttsv2\"".Length;
        while (i < raw.Length && (raw[i] == ':' || char.IsWhiteSpace(raw[i]))) i++;
        if (i >= raw.Length) return -1;
        if (raw[i] != '{' && raw[i] != '[') return -1;   // null / 字符串 / 数字 → 不量

        var start = i;
        var depth = 0;
        var inStr = false;
        var esc = false;
        for (; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inStr)
            {
                if (esc) { esc = false; continue; }
                if (ch == '\\') { esc = true; continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{' || ch == '[') depth++;
            else if (ch == '}' || ch == ']')
            {
                depth--;
                if (depth == 0) return i - start + 1;
            }
        }
        return -1;   // 括号没闭合
    }

    /// <summary>
    /// 判定 obj 里 a1ttsv2 的形态。
    /// </summary>
    public static A1TtsV2State InspectA1TtsV2(System.Text.Json.Nodes.JsonObject obj, out string reason,
                                               int rawScriptLength = -1)
    {
        reason = null;
        var hasLower = obj.ContainsKey("a1ttsv2");
        var hasUpper = obj.ContainsKey("A1ttsv2");

        if (hasLower && hasUpper)
        {
            reason = "两个别名同时存在";
            return A1TtsV2State.AliasConflict;
        }
        if (!hasLower)
        {
            if (hasUpper)
            {
                var upper = obj["A1ttsv2"];
                if (upper is null) return A1TtsV2State.ExplicitNull;
                return IsValidA1TtsV2(upper, out reason, rawScriptLength) ? A1TtsV2State.Valid : A1TtsV2State.Invalid;
            }
            return A1TtsV2State.Absent;
        }

        var node = obj["a1ttsv2"];
        if (node is null) return A1TtsV2State.ExplicitNull;

        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var strVal))
        {
            if (string.IsNullOrWhiteSpace(strVal)) return A1TtsV2State.Invalid;
            try
            {
                var inner = System.Text.Json.Nodes.JsonNode.Parse(strVal);
                if (IsValidA1TtsV2(inner, out reason, rawScriptLength)) return A1TtsV2State.StringForm;
            }
            catch (Exception e)
            {
                reason = "字符串化但解析失败:" + e.GetType().Name;
                return A1TtsV2State.Invalid;
            }
            return A1TtsV2State.Invalid;
        }

        return IsValidA1TtsV2(node, out reason, rawScriptLength) ? A1TtsV2State.Valid : A1TtsV2State.Invalid;
    }

    /// <summary>
    /// 从 content 里提取所有台词，拼成 TTS v2 需要的 a1ttsv2 结构。
    /// schema: { instruct: string(20~40字), segments: [{text, tag}] }
    /// · 只收台词（（）内的旁白跳过）
    /// · tag 从紧邻的旁白推断（冷笑/低语/颤抖…），取不到就留空（合法）
    /// 返回 null 表示没有可朗读台词。
    /// </summary>
    public static string BuildA1TtsV2(string content, string emotion)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // ★ tag 从【紧邻这句台词之前】的旁白推断：
        //   旁白里通常直接写着情绪动作（冷笑 / 低声 / 颤抖…），
        //   这是唯一能拿到局部演绎信息的来源（TTS 看不到旁白）。
        var dialogues = new List<(string Text, string Tag)>();
        var lastNarration = "";
        foreach (var sg in ParseSegments(content))
        {
            if (sg.Kind == SegKind.Narration)
            {
                var n = StripParens(sg.Text);
                if (!string.IsNullOrWhiteSpace(n)) lastNarration = n;
                continue;
            }
            if (sg.Kind != SegKind.Dialogue) continue;
            var t = StripQuotes(sg.Text);
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Length > 4096) t = t[..4096];   // schema 上限
            dialogues.Add((t, InferTag(lastNarration)));
            if (dialogues.Count >= 64) break;      // schema maxItems
        }
        if (dialogues.Count == 0) return null;

        var root = new System.Text.Json.Nodes.JsonObject
        {
            // 动作摘要来自旁白——TTS 看不到旁白，只能靠 instruct 传递神态动作
            ["instruct"] = BuildInstruct(emotion, ExtractAction(content)),
        };

        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var d in dialogues)
        {
            arr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["text"] = d.Text,
                ["tag"] = d.Tag,
            });
        }
        root["segments"] = arr;

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // ============================================================
    // tag 推断：从紧邻的旁白里提取局部演绎提示
    // ============================================================
    /// <summary>
    /// 关键词 → tag 的映射表。顺序即优先级（先匹配更具体的词）。
    /// 只收高置信度的词，宁可少给 tag，也不要给错。
    /// tag 必须 2~6 个汉字（schema 要求），空串也合法。
    /// </summary>
    private static readonly (string[] Keywords, string Tag)[] TagRules = new[]
    {
        (new[] { "冷笑", "讥", "嗤" }, "冷笑"),
        (new[] { "苦笑" }, "苦笑"),
        (new[] { "厉声", "提高声", "声音一沉", "沉声", "喝" }, "厉声"),
        (new[] { "低语", "低声", "压得很低", "压着声", "声音压", "放低声音" }, "低语"),
        (new[] { "咬牙", "一字一句", "咬住", "从牙缝" }, "咬牙"),
        (new[] { "颤", "发抖", "哽" }, "颤抖"),
        (new[] { "正色", "神色一正", "敛了笑", "收敛笑" }, "正色"),
        (new[] { "轻笑", "眉梢一挑", "眉开", "含笑", "弯眼" }, "含笑"),
        (new[] { "脸色沉", "沉脸", "脸色一沉", "怒", "不悦" }, "沉怒"),
        (new[] { "叹", "无奈" }, "含叹"),
        (new[] { "轻声", "放轻", "声音很轻", "柔声" }, "轻声"),
        (new[] { "沉默", "不语", "没有出声", "没出声", "静了片刻" }, "沉默"),
        (new[] { "随口", "漫不经心", "懒懒", "懒洋洋" }, "慵懒"),
    };

    /// <summary>
    /// 从旁白文本推断 tag。没命中就返回空串（合法，表示不加局部提示）。
    /// narration 是【紧邻这句台词之前】的那段旁白。
    /// </summary>
    public static string InferTag(string narration)
    {
        if (string.IsNullOrWhiteSpace(narration)) return "";
        foreach (var rule in TagRules)
        {
            foreach (var kw in rule.Keywords)
            {
                if (narration.Contains(kw, StringComparison.Ordinal)) return rule.Tag;
            }
        }
        return "";
    }

    /// <summary>去掉旁白外层的括号（），用于关键词匹配。</summary>
    private static string StripParens(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Trim().Trim('（', '）', '(', ')').Trim();
    }

    /// <summary>去掉台词外层的引号（“ ” " 「 」）。</summary>
    private static string StripQuotes(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Trim().Trim('“', '”', '"', '「', '」').Trim();
    }

    /// <summary>
    /// 生成 instruct。schema 限 120 字。
    /// 结构 = 情绪基调（按 emotion 映射）+ 可选的动作摘要（从旁白提取）。
    /// 加动作的理由：TTS 只看得到台词，看不到旁白，动作神态只能靠 instruct 传递。
    /// </summary>
    private static string BuildInstruct(string emotion, string action = "")
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        var baseText = e switch
        {
            "angry" or "愤怒" or "生气"
                => "带着压着的怒意，语速略快，句尾收得干脆，不拖泥带水。",
            "sad" or "难过" or "悲伤" or "委屈"
                => "语气低沉放缓，尾音略沉，带着几分说不出的疲惫。",
            "happy" or "开心" or "高兴" or "笑"
                => "语调轻快，尾音微微上扬，话里带着笑意。",
            "shy" or "害羞" or "羞"
                => "声音放轻，略带迟疑与羞赧，句尾有些不稳。",
            "surprised" or "惊讶" or "吃惊"
                => "语气里带着诧异，句首略提，节奏比平时快半拍。",
            "cold" or "冷淡" or "冷"
                => "语气平稳偏冷，起伏很少，字字清楚，不带多余情绪。",
            _ => "语气平稳自然，按角色此刻的情绪演绎，注意停顿与轻重。",
        };

        if (string.IsNullOrWhiteSpace(action)) return baseText;

        // 动作摘要接在后面；总长必须 ≤120（schema 硬上限），超了就不加
        var merged = baseText.TrimEnd('。') + "；动作：" + action;
        if (merged.Length > 120) merged = merged[..120];
        return merged;
    }

    /// <summary>
    /// 从 content 的旁白里挑一段最有信息量的当动作摘要（最长的那段，截 40 字）。
    /// 没旁白就返回空串（instruct 退化为纯情绪基调）。
    /// </summary>
    public static string ExtractAction(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var best = "";
        foreach (var sg in ParseSegments(content))
        {
            if (sg.Kind != SegKind.Narration) continue;
            var t = StripParens(sg.Text).Trim();
            if (t.Length > best.Length) best = t;
        }
        if (best.Length == 0) return "";
        // 截到句读更自然，没有句读就硬截
        if (best.Length > 40)
        {
            var cut = best[..40];
            var lastPunct = Math.Max(
                Math.Max(cut.LastIndexOf('，'), cut.LastIndexOf('。')),
                Math.Max(cut.LastIndexOf('、'), cut.LastIndexOf('；')));
            best = lastPunct > 12 ? cut[..lastPunct] : cut;
        }
        return best.Trim();
    }

}
