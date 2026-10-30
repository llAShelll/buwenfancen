using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Memory;

/// <summary>
/// 一键改名（2026-10-25 主人要）。
///
/// 背景：早期版本用 <c>NpcName</c>（带皮肤前缀，如「某某·某角色」「某某·某角色」）写盘，
/// 1.1.2 迁移过来的角色卡文件名也是皮肤名（「某某_某角色_某身份_」）。
/// 现在配置表能拿到真名（TbNpcBaseCfg.Name），所以提供一次性改名：
///
///   ① meta.json 的 NpcName
///   ② chunks.jsonl 每行的 Npc 字段
///   ③ card 目录下的文件名前缀（{旧名}-{代码}.json / .lorebook.json / .source.md）
///   ④ relation 目录里的 Name 字段
///
/// ★ 改前自动备份；只在「配置表能拿到真名 且 和旧名不同」时才动。
/// </summary>
public static class RenameTool
{
    public sealed class Result
    {
        public int RoleCount;
        public int MetaChanged;
        public int ChunkLines;
        public int FilesRenamed;
        public int RelationChanged;
        public int LorebookChanged;   // ★ 2026-10-29：角色世界书（name / keys / 自身 text）
        public int SourceChanged;     // ★ 2026-10-29：source.md 里的「你是 XXX」
        public int CardNameChanged;   // ★ 2026-10-29：卡人设「姓名」+ _来源.角色名
        public List<string> Detail = new();
        public string Error;
    }

    public static Result RunAll()
    {
        var r = new Result();
        try
        {
            var root = MemoryPaths.Root;
            var scope = MemoryPaths.Scope ?? "";
            var prefix = string.IsNullOrEmpty(scope) ? root : Path.Combine(root, scope);

            // 收集所有角色 ID：memory / relation / card 三处
            var ids = new SortedSet<string>();
            void Collect(string dir)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var n = Path.GetFileName(d);
                        if (n.Length > 0 && char.IsDigit(n[0])) ids.Add(n);
                    }
                }
                catch { }
            }
            Collect(Path.Combine(prefix, "memory"));
            Collect(Path.Combine(prefix, "relation"));
            try
            {
                var cardDir = Path.Combine(root, "card");
                if (Directory.Exists(cardDir))
                {
                    foreach (var f in Directory.GetFiles(cardDir))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-(\d+)(?:\.|$)");
                        if (m.Success) ids.Add(m.Groups[1].Value);
                    }
                }
            }
            catch { }

            r.RoleCount = ids.Count;

            foreach (var id in ids)
            {
                var real = ConfigTable.GetBaseName(id);
                if (string.IsNullOrWhiteSpace(real)) continue;   // 配置表拿不到就跳过

                // ① meta.json
                try
                {
                    var metaPath = Path.Combine(prefix, "memory", id, "meta.json");
                    if (File.Exists(metaPath))
                    {
                        var txt = File.ReadAllText(metaPath, new UTF8Encoding(false));
                        var obj = JsonNode.Parse(txt) as JsonObject;
                        var oldName = obj?["NpcName"]?.GetValue<string>() ?? "";
                        if (obj != null && oldName != real)
                        {
                            Backup(metaPath);
                            obj["NpcName"] = real;
                            File.WriteAllText(metaPath, obj.ToJsonString(new JsonSerializerOptions
                            {
                                WriteIndented = true,
                                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            }), new UTF8Encoding(false));
                            r.MetaChanged++;
                        }
                    }
                }
                catch { }

                // ② chunks.jsonl 的 Npc 字段
                try
                {
                    var cj = Path.Combine(prefix, "memory", id, "chunks.jsonl");
                    if (File.Exists(cj))
                    {
                        var lines = File.ReadAllLines(cj, new UTF8Encoding(false));
                        var changed = 0;
                        var sb = new StringBuilder();
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var node = JsonNode.Parse(line) as JsonObject;
                                var oldNpc = node?["Npc"]?.GetValue<string>() ?? "";
                                if (node != null && !string.IsNullOrEmpty(oldNpc) && oldNpc != real)
                                {
                                    node["Npc"] = real;
                                    changed++;
                                }
                                sb.AppendLine(node?.ToJsonString(new JsonSerializerOptions
                                {
                                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                }) ?? line);
                            }
                            catch { sb.AppendLine(line); }
                        }
                        if (changed > 0)
                        {
                            Backup(cj);
                            File.WriteAllText(cj, sb.ToString().TrimEnd('\n'), new UTF8Encoding(false));
                            r.ChunkLines += changed;
                        }
                    }
                }
                catch { }

                // ③ relation 目录的 Name 字段
                try
                {
                    var relPath = Path.Combine(prefix, "relation", id + ".json");
                    if (File.Exists(relPath))
                    {
                        var txt = File.ReadAllText(relPath, new UTF8Encoding(false));
                        var arr = JsonNode.Parse(txt) as JsonArray;
                        if (arr != null)
                        {
                            var changed = 0;
                            foreach (var node in arr)
                            {
                                if (node is not JsonObject o) continue;
                                var nid = o["NpcCfgId"]?.GetValue<int>() ?? 0;
                                if (nid <= 0) continue;
                                var realName = ConfigTable.GetBaseName(nid.ToString());
                                if (string.IsNullOrWhiteSpace(realName)) continue;
                                var oldName = o["Name"]?.GetValue<string>() ?? "";
                                if (oldName != realName)
                                {
                                    o["Name"] = realName;
                                    changed++;
                                }
                            }
                            if (changed > 0)
                            {
                                Backup(relPath);
                                File.WriteAllText(relPath, arr.ToJsonString(new JsonSerializerOptions
                                {
                                    WriteIndented = true,
                                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                }), new UTF8Encoding(false));
                                r.RelationChanged += changed;
                            }
                        }
                    }
                }
                catch { }

                // ★ ③.5 角色世界书：book.name / 条目 keys / 「角色自身」text 里的旧名（2026-10-29）
                //   换化身会把世界书名和 keys 写成「奈宝·芊儿4K2」，这里统一换成真名。
                try
                {
                    var cardDir0 = Path.Combine(root, "card");
                    if (Directory.Exists(cardDir0))
                    {
                        foreach (var lf in Directory.GetFiles(cardDir0))
                        {
                            var lfn = Path.GetFileName(lf);
                            if (!lfn.EndsWith(".lorebook.json", StringComparison.OrdinalIgnoreCase)) continue;
                            var m0 = System.Text.RegularExpressions.Regex.Match(
                                lfn, @"^(.*?)-(\d+)\.lorebook\.json$");
                            if (!m0.Success || m0.Groups[2].Value != id) continue;
                            var oldName = m0.Groups[1].Value.Trim();

                            var book = JsonNode.Parse(File.ReadAllText(lf, new UTF8Encoding(false))) as JsonObject;
                            if (book == null) continue;
                            var changed = false;

                            if ((book["name"]?.GetValue<string>() ?? "") != real)
                            { book["name"] = real; changed = true; }

                            if (book["entries"] is JsonArray arr0)
                            {
                                foreach (var node0 in arr0)
                                {
                                    if (node0 is not JsonObject e) continue;
                                    if (e["keys"] is JsonArray ks)
                                    {
                                        for (var ki = 0; ki < ks.Count; ki++)
                                        {
                                            var kv = ks[ki]?.GetValue<string>() ?? "";
                                            if (!string.IsNullOrWhiteSpace(oldName) && kv == oldName)
                                            { ks[ki] = real; changed = true; }
                                        }
                                    }
                                    var tx = e["text"]?.GetValue<string>() ?? "";
                                    if (!string.IsNullOrWhiteSpace(oldName) && oldName != real && tx.Contains(oldName))
                                    { e["text"] = tx.Replace(oldName, real); changed = true; }
                                }
                            }

                            if (changed)
                            {
                                Backup(lf);
                                File.WriteAllText(lf, book.ToJsonString(JsonOut), new UTF8Encoding(false));
                                r.LorebookChanged++;
                                r.Detail.Add($"世界书 {lfn}：名字/keys/自身 → {real}");
                            }
                        }
                    }
                }
                catch { }

                // ★ ③.6 source.md 里的「你是 XXX」→ 真名（2026-10-29）
                //   这是抓下来的游戏资料存档；「你是 奈宝·芊儿4K2」会让「从游戏资料填入」填错名字。
                try
                {
                    var cardDir0 = Path.Combine(root, "card");
                    if (Directory.Exists(cardDir0))
                    {
                        foreach (var sf in Directory.GetFiles(cardDir0))
                        {
                            var sfn = Path.GetFileName(sf);
                            if (!sfn.EndsWith(".source.md", StringComparison.OrdinalIgnoreCase)) continue;
                            var m0 = System.Text.RegularExpressions.Regex.Match(sfn, @"^(.*?)-(\d+)\.source\.md$");
                            if (!m0.Success || m0.Groups[2].Value != id) continue;

                            var txt0 = File.ReadAllText(sf, new UTF8Encoding(false));
                            var newTxt = System.Text.RegularExpressions.Regex.Replace(
                                txt0, @"(你是\s*)[^\r\n]+", "$1" + real.Replace("$", "$$"));
                            if (newTxt != txt0)
                            {
                                Backup(sf);
                                File.WriteAllText(sf, newTxt, new UTF8Encoding(false));
                                r.SourceChanged++;
                            }
                        }
                    }
                }
                catch { }

                // ④ card 目录文件名：{任意前缀}-{id}.{ext} → {真名}-{id}.{ext}
                try
                {
                    var cardDir = Path.Combine(root, "card");
                    if (Directory.Exists(cardDir))
                    {
                        foreach (var f in Directory.GetFiles(cardDir))
                        {
                            var fn = Path.GetFileName(f);
                            // ★ 2026-10-29：备份文件永远不动名（之前把 .改名备份_ 也一起改了，回滚时会搞混）
                            if (fn.Contains(".bak") || fn.Contains("备份")) continue;
                            var m = System.Text.RegularExpressions.Regex.Match(fn, @"^(.*?)-(\d+)(\.[^.]*.*)$");
                            if (!m.Success) continue;
                            if (m.Groups[2].Value != id) continue;
                            var suffix = m.Groups[3].Value;   // .json / .lorebook.json / .source.md …
                            var want = SanitizeFile(real) + "-" + id + suffix;
                            if (fn == want) continue;

                            var target = Path.Combine(cardDir, want);
                            // 目标已存在：不覆盖，只提示（老账号常见同名旧文件残留）
                            if (File.Exists(target))
                            {
                                r.Detail.Add($"⚠ 已有 {want}，旧文件 {fn} 没改名（可手动删掉它）");
                                continue;
                            }
                            File.Move(f, target);
                            r.FilesRenamed++;
                            r.Detail.Add($"{fn} → {want}");
                        }
                    }
                }
                catch { }

                // ⑤ 卡内的名字：_显示名 / 人设「姓名」/ _来源「角色名」（★ 2026-10-29 扩到全齐）
                try
                {
                    var mainPath = BuWenFanChen.Card.CardStore.ResolvePath(id);
                    if (mainPath != null && mainPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && mainPath.IndexOf(".lorebook.", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        var card = JsonNode.Parse(File.ReadAllText(mainPath, new UTF8Encoding(false))) as JsonObject;
                        if (card != null)
                        {
                            var changed = false;

                            var old = card["_显示名"]?.GetValue<string>() ?? "";
                            if (old != real) { card["_显示名"] = real; changed = true; }

                            if (card["人设"] is JsonObject ren0)
                            {
                                var oldN = ren0["姓名"]?.GetValue<string>() ?? "";
                                if (oldN != real) { ren0["姓名"] = real; changed = true; }
                            }

                            if (card["_来源"] is JsonObject srcMeta0)
                            {
                                var oldSrc = srcMeta0["角色名"]?.GetValue<string>() ?? "";
                                if (!string.IsNullOrWhiteSpace(oldSrc) && oldSrc != real)
                                { srcMeta0["角色名"] = real; changed = true; }
                            }

                            if (changed)
                            {
                                Backup(mainPath);
                                File.WriteAllText(mainPath, card.ToJsonString(JsonOut), new UTF8Encoding(false));
                                r.CardNameChanged++;
                            }
                        }
                    }
                }
                catch { }
            }

            r.Detail.Add($"角色 {r.RoleCount} 个｜meta 改 {r.MetaChanged}｜卡名 {r.CardNameChanged}｜"
                       + $"chunks 行改 {r.ChunkLines}｜relation 改 {r.RelationChanged}｜"
                       + $"世界书改 {r.LorebookChanged}｜资料改 {r.SourceChanged}｜文件改名 {r.FilesRenamed}");
        }
        catch (Exception e)
        {
            r.Error = e.Message;
        }
        return r;
    }

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void Backup(string path)
    {
        try
        {
            File.Copy(path, path + ".改名备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), overwrite: true);
        }
        catch { }
    }

    private static string SanitizeFile(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
