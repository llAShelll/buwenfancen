using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Memory;

/// <summary>
/// 世界书（F2）：玩家写的世界设定，多个角色共享。
///
/// 文件位置：{Root}\{Scope}\worldbook\{名字}.json
///   ★ 按存档分（2026-10-24 主人定）：A 存档一套世界，B 存档另一套
///
/// 格式：
/// {
///   "name": "某宗门",
///   "entries": [
///     { "keys": ["某宗门", "宗主"], "text": "……", "priority": 5 },
///     { "keys": ["入门"], "text": "……", "priority": 3 }
///   ]
/// }
///
/// 触发：玩家这句话（+ 最近一轮用户话）里出现 keys 任意一个词 → 该条命中
/// 注入：tail 里，放在角色卡【之前】（先背景，后人物）
/// 冲突：提示词里明确「世界书优先于游戏原文」，但「不硬性禁止角色打破常规」
/// </summary>
public static class Worldbook
{
    /// <summary>单次最多注入几条 / 总字数上限（防撑爆提示词）</summary>
    public const int MaxEntries = 5;
    public const int MaxChars = 700;

    /// <summary>单条建议字数（写多了会分散注意力）；超过 EntryHardLimit 会被截断</summary>
    public const int EntrySuggestMin = 30;
    public const int EntrySuggestMax = 120;
    public const int EntryHardLimit = 200;

    /// <summary>世界书根目录：{Root}\{Scope}\worldbook\</summary>
    public static string Dir
    {
        get
        {
            var p = MemoryPaths.Root;
            if (!string.IsNullOrEmpty(MemoryPaths.Scope))
                p = Path.Combine(p, MemoryPaths.Scope);
            return Path.Combine(p, "worldbook");
        }
    }

    // ==================== 读写 ====================

    /// <summary>列出所有世界书文件（不含扩展名）</summary>
    public static List<string> ListBooks()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(Dir)) return result;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                var n = Path.GetFileName(f);
                if (n.Contains(".bak")) continue;
                result.Add(Path.GetFileNameWithoutExtension(f));
            }
        }
        catch { }
        return result.OrderBy(x => x).ToList();
    }

    public static JsonObject LoadBook(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var p = Path.Combine(Dir, SanitizeName(name) + ".json");
            if (!File.Exists(p)) return null;
            return JsonNode.Parse(File.ReadAllText(p, new UTF8Encoding(false))) as JsonObject;
        }
        catch { return null; }
    }

    public static bool SaveBook(string name, JsonObject book)
    {
        if (string.IsNullOrWhiteSpace(name) || book == null) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            var p = Path.Combine(Dir, SanitizeName(name) + ".json");
            // 备份
            if (File.Exists(p))
            {
                try { File.Copy(p, p + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
            }
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, book.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(false));
            File.Move(tmp, p, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    public static bool DeleteBook(string name)
    {
        try
        {
            var p = Path.Combine(Dir, SanitizeName(name) + ".json");
            if (!File.Exists(p)) return false;
            File.Copy(p, p + ".删除备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
            File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 本地解析「关键词：内容」格式的设定文本，生成 entries。
    ///
    /// ★ 2026-10-25 主人要：世界书不让玩家写 JSON，让玩家写自然设定。
    ///   格式（一行一条，对不懂编程的玩家最友好）：
    ///     玄一门 / 宗门 / 外门：玄一门是正道大宗，门规森严。
    ///     妖兽 / 妖丹：低阶妖兽怕火，高阶会化形。
    ///
    ///   规则：冒号（中文：或英文:）左边是关键词（用 / 、 , 空格分隔），右边是内容。
    ///   没写关键词的行 → 整行当内容，关键词留空（AI 也会兜底不触发）。
    /// </summary>
    public static JsonObject ParseFromRaw(string raw)
    {
        var entries = new JsonArray();
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject { ["entries"] = entries };

        foreach (var rawLine in raw.Split('\n'))
        {
            var t = rawLine.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            var ci = t.IndexOf('：');
            if (ci < 0) ci = t.IndexOf(':');

            string keyPart, text;
            if (ci > 0)
            {
                keyPart = t.Substring(0, ci).Trim();
                text = t.Substring(ci + 1).Trim();
            }
            else
            {
                keyPart = "";
                text = t;
            }
            if (text.Length == 0) continue;

            var keys = new JsonArray();
            if (!string.IsNullOrEmpty(keyPart))
            {
                var parts = keyPart.Split(new[] { '/', '、', ',', '，', ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var k in parts)
                {
                    var kk = k.Trim();
                    if (kk.Length > 0) keys.Add(JsonValue.Create(kk));
                }
            }
            if (keys.Count == 0 && keyPart.Length > 0) keys.Add(JsonValue.Create(keyPart));

            entries.Add(new JsonObject
            {
                ["keys"] = keys,
                ["text"] = text,
                ["priority"] = 5,
            });
        }
        return new JsonObject { ["entries"] = entries };
    }

    // ==================== 匹配 ====================

    public sealed class Hit
    {
        public string Book { get; set; } = "";
        public string Text { get; set; } = "";
        public int Priority { get; set; }
        public List<string> MatchedKeys { get; set; } = new();
    }

    /// <summary>
    /// 按关键词匹配：把玩家这句话 + 最近一轮的话，拿去和各条 keys 比。
    /// 命中的按 priority 降序，最多 MaxEntries 条 / MaxChars 字。
    /// </summary>
    public static List<Hit> Match(string playerInput, string recentContext = null)
    {
        var hits = new List<Hit>();
        var text = ((playerInput ?? "") + "\n" + (recentContext ?? "")).Trim();
        if (string.IsNullOrEmpty(text)) return hits;

        foreach (var bookName in ListBooks())
        {
            var book = LoadBook(bookName);
            if (book == null) continue;
            if (book["entries"] is not JsonArray entries) continue;

            foreach (var node in entries)
            {
                if (node is not JsonObject e) continue;
                var body = e["text"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(body)) continue;

                var keys = new List<string>();
                if (e["keys"] is JsonArray ka)
                {
                    foreach (var k in ka)
                    {
                        var kv = k?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(kv)) keys.Add(kv);
                    }
                }
                if (keys.Count == 0) continue;

                var matched = keys.Where(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count == 0) continue;

                var pr = 3;
                try { pr = e["priority"]?.GetValue<int>() ?? 3; } catch { }

                var text2 = body.Trim();
                // 单条硬上限：超了截断（防止玩家写一大段把整块挤爆）
                if (text2.Length > EntryHardLimit)
                    text2 = text2.Substring(0, EntryHardLimit) + "…";

                hits.Add(new Hit
                {
                    Book = bookName,
                    Text = text2,
                    Priority = pr,
                    MatchedKeys = matched,
                });
            }
        }

        // 按优先级降序，截数量和字数
        var ordered = hits.OrderByDescending(h => h.Priority).ToList();
        var result = new List<Hit>();
        var total = 0;
        foreach (var h in ordered)
        {
            if (result.Count >= MaxEntries) break;
            if (total + h.Text.Length > MaxChars && result.Count > 0) break;
            result.Add(h);
            total += h.Text.Length;
        }
        return result;
    }

    /// <summary>
    /// 把命中的条目拼成注入块。
    /// 顺序：先声明「世界书优先于游戏原文」，再逐条列。
    /// </summary>
    public static string BuildBlock(List<Hit> hits)
    {
        if (hits == null || hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("## 世界设定（本局由玩家撰写，★ 优先于游戏原文：与游戏自带的背景/势力/规则冲突时，一律以本节为准）");
        sb.AppendLine("注意：这一节讲的是【世界怎么运作】，不限制角色的性格与说话方式；");
        sb.AppendLine("角色仍可按自己的性子行事，但涉及本节提到的地名、势力、规则、禁忌时，必须与本节一致。");
        foreach (var h in hits)
        {
            sb.AppendLine($"- 【{h.Book}】{h.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    // ==================== 工具 ====================

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "default";
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        var r = sb.ToString().Trim();
        return r.Length > 60 ? r.Substring(0, 60) : (r.Length == 0 ? "default" : r);
    }
}
