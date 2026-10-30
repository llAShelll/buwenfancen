using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Memory;

/// <summary>
/// 角色世界书（2026-10-25 主人要的核心功能）。
///
/// 和「世界书」的区别：
///   世界书      → 世界怎么运作（势力/地名/规则），多角色共享
///   角色世界书  → 这个角色的背景细节（关系/过往/习惯），每个角色一本
///
/// 文件：card\{角色名}-{代码}.lorebook.json
/// 格式和世界书一样：{"name":"…","entries":[{"keys":[],"text":"","priority":5}]}
///
/// 内容来源：由 RelationStore 从游戏关系网自动抓取生成（主人定：抓取游戏内容，不靠手写）。
/// 玩家可以在控制台里改。
///
/// 注入：tail 里【角色卡之后、死命令之前】—— 它是最具体的细节，离输出越近越好。
/// </summary>
public static class CharacterLorebook
{
    /// <summary>单次最多注入几条 / 总字数上限（比世界书略宽松，因为它是这个角色自己的事）</summary>
    public const int MaxEntries = 4;
    public const int MaxChars = 600;

    /// <summary>
    /// 这个角色的世界书里有没有「角色自身」条目（背景+年龄）。
    /// ★ 2026-10-27 主人定（B 方案）：删官方角色原文的前提之一——背景必须有地方顶上。
    /// </summary>
    public static bool HasSelfEntry(string npcCfgId, string npcName)
    {
        try
        {
            if (string.IsNullOrEmpty(npcCfgId)) return false;
            var path = RelationStore.LorebookPathOf(npcCfgId, npcName);
            if (!File.Exists(path)) return false;
            var book = JsonNode.Parse(File.ReadAllText(path, new UTF8Encoding(false))) as JsonObject;
            if (book?["entries"] is not JsonArray entries) return false;
            foreach (var node in entries)
            {
                if (node is not JsonObject e) continue;
                var typ = e["类型"]?.GetValue<string>() ?? "";
                if (typ != "角色自身") continue;
                var body = e["text"]?.GetValue<string>() ?? "";
                return !string.IsNullOrWhiteSpace(body);
            }
        }
        catch { }
        return false;
    }

    public static List<Worldbook.Hit> Match(string npcCfgId, string npcName, string playerInput)
    {
        var hits = new List<Worldbook.Hit>();
        if (string.IsNullOrEmpty(npcCfgId) || string.IsNullOrWhiteSpace(playerInput)) return hits;

        var path = RelationStore.LorebookPathOf(npcCfgId, npcName);
        JsonObject book = null;
        try
        {
            if (File.Exists(path))
                book = JsonNode.Parse(File.ReadAllText(path, new UTF8Encoding(false))) as JsonObject;
        }
        catch { return hits; }
        if (book?["entries"] is not JsonArray entries) return hits;

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
            // ★ 2026-10-25 主人要：条目 1「角色自身」始终注入（不需要命中关键词）
            var always = false;
            try { always = e["始终注入"]?.GetValue<bool>() ?? false; } catch { }
            var enabled = true;
            try { enabled = e["启用"]?.GetValue<bool>() ?? true; } catch { }

            if (always)
            {
                if (!enabled) continue;   // 玩家关了这条
                var prA = 6;
                try { prA = e["priority"]?.GetValue<int>() ?? 6; } catch { }
                var txtA = body.Trim();
                if (txtA.Length > Worldbook.EntryHardLimit)
                    txtA = txtA.Substring(0, Worldbook.EntryHardLimit) + "…";
                hits.Add(new Worldbook.Hit
                {
                    Book = string.IsNullOrEmpty(npcName) ? npcCfgId : npcName,
                    Text = txtA,
                    Priority = prA + 100,   // 排最前，保证不被截掉
                    MatchedKeys = new List<string> { "始终注入" },
                });
                continue;
            }

            if (keys.Count == 0) continue;

            var matched = keys.Where(k => playerInput.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 0) continue;

            var pr = 5;
            try { pr = e["priority"]?.GetValue<int>() ?? 5; } catch { }

            var txt = body.Trim();
            if (txt.Length > Worldbook.EntryHardLimit)
                txt = txt.Substring(0, Worldbook.EntryHardLimit) + "…";

            hits.Add(new Worldbook.Hit
            {
                Book = string.IsNullOrEmpty(npcName) ? npcCfgId : npcName,
                Text = txt,
                Priority = pr,
                MatchedKeys = matched,
            });
        }

        // 按优先级降序，截数量和字数
        var ordered = hits.OrderByDescending(h => h.Priority).ToList();
        var result = new List<Worldbook.Hit>();
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

    public static string BuildBlock(List<Worldbook.Hit> hits, string npcName)
    {
        if (hits == null || hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("## 关于你自己的过往与关系（说到下列人或事时才用，没提到就不要主动铺陈）");
        sb.AppendLine("注意：这些是【你自己的记忆】，不是玩家的。写的时候用「我」的视角自然带出；");
        sb.AppendLine("如果玩家问起相关的人或事，按这里的设定回答，不要自己另编。");
        foreach (var h in hits)
            sb.AppendLine($"- {h.Text}");
        return sb.ToString().TrimEnd();
    }
}
