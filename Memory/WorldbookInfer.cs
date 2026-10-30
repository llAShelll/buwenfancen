using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Memory;

/// <summary>
/// 世界书推断（F2 扩展，2026-10-24 主人要 B 方案）。
///
/// 游戏本身没有结构化的世界观数据（只有一句「你活在修仙世界里」188 字），
/// 所以只能「从已有文本推断」：
///   输入：已抓到的角色资料（CardSourceStore 的 source.md）+ 最近对话片段
///   输出：一份世界书草稿（势力 / 地点 / 规则 / 禁忌 / 境界体系）
///
/// ★ 明确这是推断不是读取：可能不准、可能编，玩家要自己过一遍。
/// ★ 字数按 Worldbook 的约定：单条 30~120 字，总数不超过 700。
/// </summary>
public static class WorldbookInfer
{
    private const string SystemPrompt = @"你是世界设定整理员。下面给你一些【游戏里已有的角色资料和对话片段】。
请从中提取出这个世界共通的东西，产出一份「世界书」，只输出严格 JSON（不要 markdown、不要解释）：

{
  ""name"": ""世界书"",
  ""entries"": [
    { ""keys"": [""触发词1"", ""触发词2""], ""text"": ""这条设定的内容"", ""priority"": 5 }
  ]
}

要求：
- 只提取【世界观层面】的内容：势力/宗门、地名、境界体系、规矩、禁忌、势力之间的关系
- 【不要】写某个角色个人的性格、外貌、口吻 —— 那些属于角色卡，不进世界书
- 每条 text 控制在 30~120 字，写具体、能落地（不要写「民风淳朴」这种空话）
- keys 写 1~4 个对话里可能出现的触发词（势力名、地名、功法名、称谓等）
- priority：1~10。核心设定（主城、主要势力、境界体系）给 5~8；次要细节给 2~4
- 最多 8 条。资料里看不出来的不要编
- 如果资料里几乎没有世界观信息，就返回 {""name"":""世界书"",""entries"":[]}
- 全部简体中文";

    /// <summary>
    /// 从已有文本推断世界书。成功返回 (条目 JSON 文本, 用到的资料长度)。
    /// </summary>
    public static async Task<(bool Ok, string Data, int MaterialChars)> InferAsync(int maxBooks = 5)
    {
        var material = CollectMaterial(maxBooks);
        if (material.Length < 100)
            return (false, "可用的角色资料太少，先进游戏和几个角色聊几句，插件会自动抓资料", material.Length);

        var cfg = MemoryEngine.GetLlmConfig(null);
        if (!cfg.IsValid) return (false, "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey", material.Length);

        // 角色卡专用模型（如果配了）也适用于这里
        if (!string.IsNullOrWhiteSpace(CardModelOrEmpty()))
            cfg.Model = CardModelOrEmpty();

        if (material.Length > 12000) material = material.Substring(0, 12000);

        var (content, err) = await LlmClient.CompleteAsync(cfg, SystemPrompt, material, 3000);
        if (content == null) return (false, "模型调用失败：" + err, material.Length);

        var t = content.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
            t = t.Trim();
        }
        var a = t.IndexOf('{');
        var z = t.LastIndexOf('}');
        if (a < 0 || z <= a) return (false, "模型没有返回 JSON：" + Truncate(content, 150), material.Length);
        t = t.Substring(a, z - a + 1);

        try
        {
            var node = JsonNode.Parse(t) as JsonObject;
            if (node == null) return (false, "返回的不是 JSON 对象", material.Length);
            if (!(node["entries"] is JsonArray arr))
                return (false, "返回缺 entries 字段", material.Length);
            if (arr.Count == 0)
                return (false, "资料里没提取出世界观内容（可能角色资料都只有个人设定）", material.Length);

            // 规整一下：补 priority、限制条数、单条截断
            var cleaned = new JsonArray();
            var count = 0;
            foreach (var e in arr)
            {
                if (e is not JsonObject o) continue;
                var txt = o["text"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(txt)) continue;
                if (txt.Length > Worldbook.EntryHardLimit)
                    txt = txt.Substring(0, Worldbook.EntryHardLimit) + "…";
                o["text"] = txt;
                if (!o.ContainsKey("priority")) o["priority"] = 4;
                if (!o.ContainsKey("keys"))
                {
                    // 没给触发词就跳过（没触发词的条目永远命中不了）
                    continue;
                }
                // ★ 2026-10-28 修 BUG（The node already has a parent）：
                //   o 还在 arr（node["entries"]）里、已经有父节点，直接 Add 到 cleaned 会抛。
                //   必须先做一份独立副本（序列化再解析 = JsonNode 的克隆）。
                cleaned.Add(JsonNode.Parse(o.ToJsonString()) as JsonObject);
                count++;
                if (count >= 8) break;
            }
            node["entries"] = cleaned;

            return (true, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), material.Length);
        }
        catch (Exception e)
        {
            return (false, "解析失败：" + e.Message, material.Length);
        }
    }

    /// <summary>
    /// 收集资料：把所有已抓的角色资料拼起来（取最近的几个角色）。
    /// </summary>
    private static string CollectMaterial(int maxBooks)
    {
        var sb = new StringBuilder();
        try
        {
            var cardDir = Path.Combine(MemoryPaths.Root, "card");
            if (!Directory.Exists(cardDir)) return "";

            var files = Directory.GetFiles(cardDir, "*.source.md")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(Math.Max(1, maxBooks))
                .ToList();

            foreach (var f in files)
            {
                try
                {
                    var txt = File.ReadAllText(f, new UTF8Encoding(false)).Trim();
                    if (txt.Length == 0) continue;
                    var name = Path.GetFileNameWithoutExtension(f).Replace(".source", "");
                    sb.AppendLine($"【{name}】");
                    sb.AppendLine(txt);
                    sb.AppendLine();
                }
                catch { }
            }
        }
        catch { }
        return sb.ToString();
    }

    private static string CardModelOrEmpty()
    {
        try { return PromptConfig.CardModel; } catch { return ""; }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
