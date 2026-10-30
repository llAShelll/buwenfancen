using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Memory;

/// <summary>
/// 关系网抓取（角色世界书的数据来源）。
///
/// 2026-10-25 主人定：角色世界书要「抓取游戏内容来写」，内容是【关系】和【角色信息】。
///
/// 数据来源：游戏每轮 CompleteBudgeted 都会传关系网，里面有
///   关系类型（师父/道侣/徒弟/亲友/仇敌）
///   + 每个关系的 NpcCfgId、名字、称号、背景、说话风格
///
/// ★ 注意这是【玩家视角】的关系网（玩家认识谁），不是【当前 NPC 视角】——
///   NpcRelationManager 在 IL2CPP 下拿不到（实测 FindObjectOfType 返回 null）。
///   所以生成的条目要写成「XXX 是玩家的道侣」，不能写成「XXX 是你的道侣」，
///   否则会让当前角色误认关系。
/// </summary>
public static class RelationStore
{
    public sealed class Item
    {
        public string Type { get; set; } = "";       // 师父 / 道侣 / 徒弟 / 亲友 / 仇敌
        public int NpcCfgId { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Background { get; set; } = "";
        public string SpeakStyle { get; set; } = "";

        /// <summary>兴趣（游戏里是物品类型 ID 列表，转成名称后存这里）</summary>
        public List<string> Interests { get; set; } = new();
    }

    /// <summary>关系类型 → 生成条目的优先级（越亲越重要）</summary>
    private static int PriorityOf(string type) => type switch
    {
        "道侣" => 8,
        "师父" => 7,
        "徒弟" => 6,
        "亲友" => 5,
        "仇敌" => 4,
        _ => 3,
    };

    // ★ 2026-10-25 主人定：亲密度不抓、也不写进角色世界书
    //   （实测游戏里大多 Intimacy=0，没什么可用的信息）

    private static string PathOf(string npcCfgId)
    {
        var p = MemoryPaths.Root;
        if (!string.IsNullOrEmpty(MemoryPaths.Scope))
            p = Path.Combine(p, MemoryPaths.Scope);
        var dir = Path.Combine(p, "relation");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Sanitize(npcCfgId) + ".json");
    }

    public static void Save(string npcCfgId, List<Item> items)
    {
        if (string.IsNullOrEmpty(npcCfgId) || items == null) return;
        try
        {
            // 内容没变就不写盘（这个函数每轮都会被调）
            var p = PathOf(npcCfgId);
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            if (File.Exists(p))
            {
                try
                {
                    if (File.ReadAllText(p, new UTF8Encoding(false)) == json) return;
                }
                catch { }
            }
            File.WriteAllText(p, json, new UTF8Encoding(false));
        }
        catch { }
    }

    public static List<Item> Load(string npcCfgId)
    {
        try
        {
            var p = PathOf(npcCfgId);
            if (!File.Exists(p)) return new List<Item>();
            var list = JsonSerializer.Deserialize<List<Item>>(
                File.ReadAllText(p, new UTF8Encoding(false)),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return list ?? new List<Item>();
        }
        catch { return new List<Item>(); }
    }

    /// <summary>
    /// 从抓到的关系网生成「角色世界书」。
    /// 返回可直接写进 .lorebook.json 的 JsonObject。
    /// </summary>
    /// <summary>
    /// 从抓到的关系网生成「角色世界书」。
    /// 结构：角色自己的条目 + 关系条目（都标 来源=auto），
    /// 手写条目（来源=manual）由 WriteLorebookIfAbsent 从旧文件保留。
    /// </summary>
    public static JsonObject BuildLorebook(string npcCfgId, string npcName, int selfAge = -1, string selfBackground = "")
    {
        var book = new JsonObject
        {
            ["name"] = string.IsNullOrEmpty(npcName) ? (npcCfgId ?? "") : npcName,
            ["entries"] = new JsonArray(),
        };
        var entries = (JsonArray)book["entries"]!;

        // ===== ① 角色自己（自动）=====
        if (selfAge > 0 || !string.IsNullOrWhiteSpace(selfBackground))
        {
            var selfKeys = new JsonArray();
            if (!string.IsNullOrWhiteSpace(npcName)) selfKeys.Add(npcName);
            var selfText = new StringBuilder();
            if (selfAge > 0) selfText.Append("你今年 ").Append(selfAge).Append(" 岁。");
            if (!string.IsNullOrWhiteSpace(selfBackground))
            {
                // ★ 人称统一：背景原文常以角色名开头（"某某是某堂执事弟子…"），
                //   和前面「你今年 75 岁」拼起来会变成「你…某某…」，所以把名字替换成「你」
                var bg = selfBackground.Trim();
                if (!string.IsNullOrWhiteSpace(npcName))
                    bg = bg.Replace(npcName, "你");
                // ★ 第三人称也换成「你」（背景原文常写「他会…」「她性子…」）
                bg = bg.Replace("她", "你").Replace("他", "你");
                selfText.Append(bg);
            }
            entries.Add(new JsonObject
            {
                ["keys"] = selfKeys,
                ["text"] = selfText.ToString(),
                ["priority"] = 6,
                ["来源"] = "auto",
                ["类型"] = "角色自身",
                ["始终注入"] = true,
                ["启用"] = true,
            });
        }

        // ===== ② 关系（自动）=====
        var seen = new Dictionary<int, Item>();
        foreach (var it in Load(npcCfgId))
        {
            if (it.NpcCfgId <= 0 || string.IsNullOrWhiteSpace(it.Name)) continue;
            if (seen.TryGetValue(it.NpcCfgId, out var old))
            {
                if (PriorityOf(it.Type) > PriorityOf(old.Type)) seen[it.NpcCfgId] = it;
            }
            else seen[it.NpcCfgId] = it;
        }

        foreach (var it in seen.Values.OrderByDescending(x => PriorityOf(x.Type)))
        {
            var keys = new JsonArray();
            var n = it.Name.Trim();
            if (!string.IsNullOrEmpty(n)) keys.Add(n);
            var dot = n.LastIndexOf('·');
            if (dot >= 0 && dot + 1 < n.Length)
            {
                var shortName = n.Substring(dot + 1).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Trim();
                if (shortName.Length >= 2 && shortName != n) keys.Add(shortName);
            }
            if (!string.IsNullOrWhiteSpace(it.Title)) keys.Add(it.Title.Trim());

            // ★ 正文开头标【关系类型】，比藏在句尾显眼
            var sb = new StringBuilder();
            sb.Append("【").Append(it.Type).Append("】").Append(it.Name);
            if (!string.IsNullOrWhiteSpace(it.Title)) sb.Append("（").Append(it.Title).Append("）");
            sb.Append("。");
            if (!string.IsNullOrWhiteSpace(it.Background))
            {
                var bg = it.Background.Trim();
                if (bg.Length > 150) bg = bg.Substring(0, 150) + "…";
                sb.Append(bg);
            }

            entries.Add(new JsonObject
            {
                ["keys"] = keys,
                ["text"] = sb.ToString(),
                ["priority"] = PriorityOf(it.Type),
                ["来源"] = "auto",
                ["类型"] = "关系",
            });
        }
        return book;
    }

    /// <summary>
    /// 生成并写入 {角色名}-{代码}.lorebook.json。
    ///
    /// ★ 2026-10-25 改：不再「只在首次生成」。
    ///   关系是会变的（今天亲友、明天道侣），所以每次抓到关系后比对：
    ///     · 文件不存在 → 生成
    ///     · 关系条目有变化（新增/删除） → 备份旧的再覆盖
    ///     · 完全没变 → 不动
    ///   overwrite=true 时无条件覆盖（控制台「重新生成」按钮用）。
    /// </summary>
    public static bool WriteLorebookIfAbsent(string npcCfgId, string npcName, bool overwrite = false,
        int selfAge = -1, string selfBackground = "")
    {
        try
        {
            var p = LorebookPathOf(npcCfgId, npcName);
            var book = BuildLorebook(npcCfgId, npcName, selfAge, selfBackground);

            // ★ 保留玩家手写的条目（来源=manual），只重写自动条目
            if (book["entries"] is JsonArray arr)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var oldBook = JsonNode.Parse(File.ReadAllText(p, new UTF8Encoding(false))) as JsonObject;
                        if (oldBook?["entries"] is JsonArray oldArr)
                        {
                            foreach (var node in oldArr)
                            {
                                if (node is JsonObject o && o["来源"]?.GetValue<string>() == "manual")
                                    arr.Add(JsonNode.Parse(o.ToJsonString()) as JsonObject);
                            }
                        }
                    }
                }
                catch { }
                if (arr.Count == 0) return false;
            }
            else return false;

            var newJson = book.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            if (File.Exists(p) && !overwrite)
            {
                try
                {
                    var oldText = File.ReadAllText(p, new UTF8Encoding(false));
                    var oldBook = JsonNode.Parse(oldText) as JsonObject;
                    var oldKeys = CollectKeySignature(oldBook);
                    var newKeys = CollectKeySignature(book);
                    if (oldKeys == newKeys) return false;
                }
                catch { }
                try
                {
                    File.Copy(p, p + ".关系变更备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
                }
                catch { }
            }

            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, newJson, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 只更新世界书里的「角色自身」条目（年龄+背景），不动关系/手写条目。
    /// ★ 2026-10-27（B 方案）：「从游戏资料填入」把背景填到这里，而不是卡字段。
    /// </summary>
    public static bool UpdateSelfEntry(string npcCfgId, string npcName, int selfAge, string selfBackground)
    {
        try
        {
            if (string.IsNullOrEmpty(npcCfgId)) return false;
            var p = LorebookPathOf(npcCfgId, npcName);
            JsonObject book = null;
            if (File.Exists(p))
                book = JsonNode.Parse(File.ReadAllText(p, new UTF8Encoding(false))) as JsonObject;
            if (book == null) book = new JsonObject { ["name"] = npcName ?? npcCfgId, ["entries"] = new JsonArray() };
            if (book["entries"] is not JsonArray arr) { arr = new JsonArray(); book["entries"] = arr; }

            var sb = new StringBuilder();
            if (selfAge > 0) sb.Append("你今年 ").Append(selfAge).Append(" 岁。");
            if (!string.IsNullOrWhiteSpace(selfBackground))
            {
                var bg = selfBackground.Trim();
                if (!string.IsNullOrWhiteSpace(npcName)) bg = bg.Replace(npcName, "你");
                bg = bg.Replace("她", "你").Replace("他", "你");
                sb.Append(bg);
            }
            var text = sb.ToString().Trim();
            if (text.Length == 0) return false;

            var keys = new JsonArray();
            if (!string.IsNullOrWhiteSpace(npcName)) keys.Add(npcName);

            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            foreach (var node in arr)
            {
                if (node is not JsonObject e) continue;
                if (e["类型"]?.GetValue<string>() != "角色自身") continue;
                e["text"] = text;
                e["keys"] = keys;
                File.WriteAllText(p, book.ToJsonString(jsonOpts), new UTF8Encoding(false));
                return true;
            }
            arr.Add(new JsonObject
            {
                ["keys"] = keys,
                ["text"] = text,
                ["priority"] = 6,
                ["来源"] = "auto",
                ["类型"] = "角色自身",
                ["始终注入"] = true,
                ["启用"] = true,
            });
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, book.ToJsonString(jsonOpts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>把所有条目的 keys 拼成一个签名串，用来判断关系有没有变</summary>
    private static string CollectKeySignature(JsonObject book)
    {
        try
        {
            if (book?["entries"] is not JsonArray arr) return "";
            var parts = new List<string>();
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                if (o["keys"] is JsonArray ks)
                {
                    var keys = new List<string>();
                    foreach (var k in ks)
                    {
                        var kv = k?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(kv)) keys.Add(kv);
                    }
                    var typ = o["类型"]?.GetValue<string>() ?? "";
                    var always = false;
                    try { always = o["始终注入"]?.GetValue<bool>() ?? false; } catch { }
                    parts.Add(string.Join("|", keys) + "#" + typ + "#" + (always ? "1" : "0"));
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
        catch { return ""; }
    }

    /// <summary>角色世界书文件路径：card\{角色名}-{代码}.lorebook.json</summary>
    public static string LorebookPathOf(string npcCfgId, string npcName)
    {
        var cardJson = MemoryPaths.CardPath(npcCfgId);
        var dir = Path.GetDirectoryName(cardJson) ?? ".";
        var safe = string.IsNullOrWhiteSpace(npcName) ? "" : SanitizeFile(npcName);
        var baseName = string.IsNullOrEmpty(safe) ? npcCfgId : safe + "-" + npcCfgId;
        return Path.Combine(dir, baseName + ".lorebook.json");
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static string SanitizeFile(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
