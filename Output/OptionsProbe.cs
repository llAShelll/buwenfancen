namespace BuWenFanChen.Output;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;

/// <summary>
/// 四选一对话选项（2026-11-03 实验）：
/// 让模型在 JSON 顶层多给一个 "opts": ["…","…","…","…"]，
/// 我们在输出侧摘掉它 —— 不进 content、不进 a1ttsv2、不进历史，
/// 只记日志 + 落盘（{Root}\opts_log.jsonl），等验证稳定后再做弹窗面板。
///
/// ★ 2026-11-04 关键修正：语音 MOD 会在 AddNpcChatMessage 之前做
///   HistoryEnvelope.NormalizeOrWrap，把 a1ttsv2 和 opts 一起剥掉
///   （TTS 日志实证：502 字 a1ttsv2=valid → 264 字 a1ttsv2=absent）。
///   所以 raw 里找不到 opts 时，要去 RawReplyStore（LogResponse 截下的原始输出）里找。
///
/// 摘取方式：字符串手术（不整体反序列化再序列化），
///   因为重新序列化会动到所有转义/空白，可能把模型辛苦写对的 content 弄坏。
/// </summary>
internal static class OptionsProbe
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·选项");
    private static readonly object Gate = new();

    /// <summary>收尾句标记：拿去判断「四条是不是都在把话聊死」。</summary>
    private static readonly string[] Closers =
    {
        "先走了", "先告辞", "告辞", "改日", "再会", "回见", "不打扰",
        "就此别过", "下次再说", "改天", "先回去", "回去歇",
    };

    /// <summary>
    /// 从 npcRawOutput 里摘掉顶层 "opts"，返回（可能缩短的）字符串。
    /// 摘不到就原样返回，绝不抛异常。
    /// </summary>
    internal static string TakeAndStrip(string raw, int turn)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        try
        {
            // ① 正常路径：raw 里就有 opts（没装语音 MOD 时是这样）
            if (TryLocate(raw, out var keyIdx, out var arrEnd, out var opts))
            {
                var cleaned = Strip(raw, keyIdx, arrEnd);
                Report(turn, opts, cleaned.Length, "raw");
                return cleaned;
            }

            // ② 语音 MOD 已经把 raw 洗过了 → 去原始输出里找（Peek 不清空，好感解析还要用）
            var origin = RawReplyStore.Peek();
            if (!string.IsNullOrEmpty(origin) && TryLocate(origin, out _, out _, out var opts2))
            {
                Report(turn, opts2, raw.Length, "origin（raw 已被语音 MOD 洗过，只记录不摘）");
                return raw;
            }

            // ③ 都没有 → 留痕，方便下次排查
            var head = raw.Length > 400 ? raw[..400] : raw;
            var originHead = string.IsNullOrEmpty(origin) ? "（无）"
                : (origin.Length > 200 ? origin[..200] : origin);
            Log.LogInfo($"[选项] 第 {turn} 轮：没有 opts。raw 前 400 字：\n{head}\n原始输出前 200 字：\n{originHead}");
            return raw;
        }
        catch (Exception e)
        {
            Log.LogWarning($"[选项] 摘取异常（{e.GetType().Name}: {e.Message}），本轮 opts 保留未动");
            return raw;
        }
    }

    // ==================== 定位与摘除 ====================

    /// <summary>在字符串里定位顶层 "opts":[…] 并解析出数组内容。</summary>
    private static bool TryLocate(string s, out int keyIdx, out int arrEnd, out List<string> opts)
    {
        keyIdx = -1; arrEnd = -1; opts = null;
        if (string.IsNullOrEmpty(s)) return false;
        var jsonStart = s.IndexOf('{');
        if (jsonStart < 0) return false;

        keyIdx = FindKeyOutsideString(s, "\"opts\"", jsonStart);
        if (keyIdx < 0) return false;

        // "opts" 后面必须是 : [
        var i = keyIdx + "\"opts\"".Length;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length || s[i] != ':') return false;
        i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length || s[i] != '[') return false;

        arrEnd = MatchBracket(s, i);
        if (arrEnd < 0) return false;

        opts = ParseArray(s.Substring(i, arrEnd - i + 1));
        return opts.Count > 0;
    }

    /// <summary>删掉 "opts":[...]（连一侧的逗号）。</summary>
    private static string Strip(string s, int keyIdx, int arrEnd)
    {
        int delStart = keyIdx;
        int k = keyIdx - 1;
        while (k >= 0 && char.IsWhiteSpace(s[k])) k--;
        if (k >= 0 && s[k] == ',')
        {
            delStart = k;   // 吞掉前一个逗号："a":1, "opts":[…]
        }
        else
        {
            // 前面是 '{'（opts 是第一个字段）→ 吞掉后面的逗号
            int e = arrEnd + 1;
            while (e < s.Length && char.IsWhiteSpace(s[e])) e++;
            if (e < s.Length && s[e] == ',') arrEnd = e;
        }
        var cleaned = s[..delStart] + s[(arrEnd + 1)..];

        // 自查：摘除后还应当是合法 JSON。不合法说明【原串本身】就有瑕疵
        // （双逗号/尾逗号之类），交给后面的修补解析处理，这里只留痕。
        try { JsonNode.Parse(cleaned); }
        catch { Log.LogWarning("[选项] 摘除 opts 后 JSON 仍不合法（原串本身有瑕疵），已交修补解析"); }

        return cleaned;
    }

    // ==================== 统计与落盘 ====================

    private static void Report(int turn, List<string> opts, int rawLen, string source)
    {
        var flags = BuildFlags(opts);

        var flagText = flags.Count == 0 ? "检查通过" : string.Join("；", flags);
        var sb = new StringBuilder();
        sb.Append($"[选项] 第 {turn} 轮 {opts.Count} 条（来源：{source}；{flagText}）");
        for (int n = 0; n < opts.Count; n++)
            sb.Append($"\n[选项]   {(char)('A' + n)}. {opts[n]}");
        sb.Append($"\n[选项]   回复 {rawLen} 字");
        Log.LogInfo(sb.ToString());

        AppendJsonl(turn, opts, flags, source);

        // ★ 2026-11-04：推到屏幕左下的常驻显示框（IMGUI，不碰游戏 UI）
        //   传原始文本，HUD 自己加 A./B. 前缀；点击时用原始文本写进输入框
        try
        {
            BuWenFanChen.OptionsHud.Show(opts.ToArray());
        }
        catch { }
    }

    private static List<string> BuildFlags(List<string> opts)
    {
        var flags = new List<string>();
        if (opts.Count != 4) flags.Add($"条数={opts.Count}");
        for (int n = 0; n < opts.Count; n++)
        {
            var s = opts[n];
            if (s.Length < 8 || s.Length > 40) flags.Add($"{(char)('A' + n)}长度{s.Length}");
            if (s.Contains('（') || s.Contains('(') || s.Contains('《'))
                flags.Add($"{(char)('A' + n)}含旁白/标点");
            if (s.Contains('"') || s.Contains('“')) flags.Add($"{(char)('A' + n)}含引号");
        }
        int closers = 0;
        foreach (var s in opts) if (ContainsAny(s, Closers)) closers++;
        if (closers >= 2) flags.Add($"收尾句{closers}条");
        return flags;
    }

    private static bool ContainsAny(string s, string[] needles)
    {
        foreach (var n in needles)
            if (s.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void AppendJsonl(int turn, List<string> opts, List<string> flags, string source)
    {
        try
        {
            var dir = BuWenFanChen.Memory.MemoryPaths.Root;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "opts_log.jsonl");
            var obj = new JsonObject
            {
                ["turn"] = turn,
                ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["source"] = source,
                ["opts"] = new JsonArray(opts.ConvertAll(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["flags"] = new JsonArray(flags.ConvertAll(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            };
            var line = obj.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
            });
            lock (Gate)
                File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
        }
        catch { }
    }

    private static List<string> ParseArray(string arrText)
    {
        var list = new List<string>();
        try
        {
            if (JsonNode.Parse(arrText) is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        list.Add(s.Trim());
                }
            }
        }
        catch
        {
            // 数组解析失败 → 用最朴素的分割兜底
            var body = arrText.Trim('[', ']');
            foreach (var part in body.Split(','))
            {
                var s = part.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    // ==================== 字符串扫描工具 ====================

    /// <summary>找 key 出现位置，且该位置不在字符串字面量里。</summary>
    private static int FindKeyOutsideString(string s, string key, int from)
    {
        if (from < 0) from = 0;
        int i = from;
        while ((i = s.IndexOf(key, i, StringComparison.Ordinal)) >= 0)
        {
            if (!InString(s, i)) return i;
            i += key.Length;
        }
        return -1;
    }

    /// <summary>位置 pos 是否落在字符串里（数引号奇偶，跳过转义）。</summary>
    private static bool InString(string s, int pos)
    {
        bool inStr = false;
        for (int k = 0; k < pos && k < s.Length; k++)
        {
            char c = s[k];
            if (inStr && c == '\\') { k++; continue; }
            if (c == '"') inStr = !inStr;
        }
        return inStr;
    }

    /// <summary>从 open 处的 '[' 配平到对应的 ']'（跳过字符串内的括号）。</summary>
    private static int MatchBracket(string s, int open)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
