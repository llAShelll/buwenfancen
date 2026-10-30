using System.Text;
using System.Text.Json.Nodes;
using BuWenFanChen.Memory;

namespace BuWenFanChen.Card;

/// <summary>
/// 角色卡读写（阶段 5-A）。
///
/// 文件位置：{Root}\{Scope}\card\{NpcCfgId}.json
/// 格式与 1.1.2 完全兼容（四大块：口吻 / 自定义设定 / NSFW / 人设，外加 _来源 底稿和 启用 开关）。
///
/// 为什么不定义强类型类：角色卡字段会随版本增加，
/// 用 JsonObject 原样读写才能保证「旧卡新卡都能读、不认识的字段不丢」。
/// </summary>
public static class CardStore
{
    private static readonly System.Text.Json.JsonSerializerOptions PrettyOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 卡片路径。新格式：{角色名}-{角色代码}.json（2026-10-24 主人定）。
    /// 传 npcName 时用新格式；不传则退回旧格式 {角色代码}.json（兼容）。
    /// </summary>
    public static string PathOf(string npcCfgId, string npcName = null)
    {
        var plain = MemoryPaths.CardPath(npcCfgId);
        if (string.IsNullOrWhiteSpace(npcName)) return plain;

        var dir = System.IO.Path.GetDirectoryName(plain) ?? ".";
        var safe = SanitizeFileName(npcName);
        if (string.IsNullOrEmpty(safe)) return plain;
        return System.IO.Path.Combine(dir, safe + "-" + npcCfgId + ".json");
    }

    /// <summary>
    /// 找这张卡实际存在的位置（支持一个角色多张卡，2026-10-24 主人要）。
    ///
    /// 命名约定：
    ///   默认卡   {角色名}-{代码}.json
    ///   备用卡   {角色名}-{代码}-{卡名}.json
    ///   旧格式   {代码}.json（兼容）
    ///
    /// 选哪张：config.json 的 cardPick[{代码}] 指定卡名；
    ///        没指定或指定的卡不存在 → 用默认卡；再没有 → 用任意一张；最后退回旧格式。
    /// </summary>
    public static string ResolvePath(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return null;
        try
        {
            var cards = ListCards(npcCfgId);

            // ① 玩家在这张存档里指定的卡（按存档存，A/B 存档可以选不同卡）
            var pick = GetPick(npcCfgId);
            if (!string.IsNullOrWhiteSpace(pick))
            {
                var hit = cards.FirstOrDefault(c => c.Name == pick);
                if (hit.Path != null) return hit.Path;
            }

            // ② 默认卡（没有卡名后缀的那张）
            var def = cards.FirstOrDefault(c => string.IsNullOrEmpty(c.Name));
            if (def.Path != null) return def.Path;

            // ③ 任意一张
            if (cards.Count > 0) return cards[0].Path;

            // ④ 旧格式
            var plain = MemoryPaths.CardPath(npcCfgId);
            if (System.IO.File.Exists(plain)) return plain;
        }
        catch { }
        return null;
    }

    // ==================== 每存档选卡（2026-10-24）====================

    /// <summary>
    /// 每存档的选卡表：{Root}\{Scope}\cardPick.json
    /// 结构：{"150101": "日常", "140001": ""}
    /// </summary>
    private static string PickFilePath()
    {
        var root = MemoryPaths.Root;
        var p = root;
        if (!string.IsNullOrEmpty(MemoryPaths.Scope))
            p = System.IO.Path.Combine(p, MemoryPaths.Scope);
        return System.IO.Path.Combine(p, "cardPick.json");
    }

    private static System.Text.Json.Nodes.JsonObject LoadPickTable()
    {
        try
        {
            var p = PickFilePath();
            if (!System.IO.File.Exists(p)) return new System.Text.Json.Nodes.JsonObject();
            var node = JsonNode.Parse(System.IO.File.ReadAllText(p, new UTF8Encoding(false)));
            return node as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch { return new System.Text.Json.Nodes.JsonObject(); }
    }

    /// <summary>这个角色在当前存档里用哪张卡（空 = 默认卡）</summary>
    public static string GetPick(string npcCfgId)
    {
        try
        {
            var t = LoadPickTable();
            return t[npcCfgId]?.GetValue<string>() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>设置这个角色在当前存档用哪张卡（传空 = 用默认卡）</summary>
    public static bool SetPick(string npcCfgId, string cardName)
    {
        try
        {
            var t = LoadPickTable();
            if (string.IsNullOrWhiteSpace(cardName))
                t.Remove(npcCfgId);
            else
                t[npcCfgId] = cardName;

            var p = PickFilePath();
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(p,
                t.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 列出这个角色的所有卡：[(卡名, 路径)]。
    /// 卡名为空 = 默认卡；非空 = 备用卡的名字。
    /// </summary>
    public static List<(string Name, string Path)> ListCards(string npcCfgId)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(npcCfgId)) return result;
        try
        {
            var plain = MemoryPaths.CardPath(npcCfgId);
            var dir = System.IO.Path.GetDirectoryName(plain);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return result;

            var prefix = "-" + npcCfgId;
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.json"))
            {
                var n = System.IO.Path.GetFileNameWithoutExtension(f);
                if (n.Contains(".bak") || n.Contains(".优化中")) continue;

                // 形如 {名字}-{代码} 或 {名字}-{代码}-{卡名}
                var idx = n.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0) continue;
                // 前缀部分必须非空（有角色名）
                if (idx == 0) continue;

                var after = n.Substring(idx + prefix.Length);
                // after 要么空（默认卡），要么以 - 开头（卡名）
                if (after.Length > 0 && !after.StartsWith("-")) continue;
                var cardName = after.Length > 0 ? after.Substring(1) : "";
                result.Add((cardName, f));
            }
        }
        catch { }
        // 默认卡排前面
        return result.OrderBy(x => x.Item1.Length == 0 ? 0 : 1).ThenBy(x => x.Item1).ToList();
    }

    /// <summary>文件名净化：去掉 Windows 非法字符</summary>
    internal static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(bad, c) >= 0 || c == '\\' || c == '/' ? '_' : c);
        var result = sb.ToString().Trim();
        // 太长会被文件系统截断，限 40 字
        return result.Length > 40 ? result.Substring(0, 40) : result;
    }

    /// <summary>从卡片自身或 meta.json 猜角色名（用于决定文件名）</summary>
    private static string GuessName(string npcCfgId, JsonObject card)
    {
        // ★ 玩家手动改过的卡名优先（控制台「改卡名」写入 _显示名）
        try
        {
            var manual = card?["_显示名"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(manual)) return manual;
        }
        catch { }

        // ★ 2026-10-25：优先配置表初始名（最准，不带皮肤前缀）
        try
        {
            var fromTable = ConfigTable.GetBaseName(npcCfgId);
            if (!string.IsNullOrWhiteSpace(fromTable)) return fromTable;
        }
        catch { }

        try
        {
            var n = (card?["_来源"] as JsonObject)?["角色名"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        catch { }

        // 兜底：读 meta.json
        try
        {
            var metaPath = System.IO.Path.Combine(MemoryPaths.MemDir(npcCfgId), "meta.json");
            if (System.IO.File.Exists(metaPath))
            {
                var meta = JsonNode.Parse(System.IO.File.ReadAllText(metaPath, new UTF8Encoding(false)));
                var n2 = meta?["NpcName"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(n2)) return n2;
            }
        }
        catch { }
        return null;
    }

    /// <summary>读一张卡；没有或读不动返回 null。</summary>
    public static JsonObject Load(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return null;
        try
        {
            var p = ResolvePath(npcCfgId);
            if (p == null) return null;
            var text = System.IO.File.ReadAllText(p, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(text)) return null;
            var card = JsonNode.Parse(text) as JsonObject;
            if (card != null) NormalizeRenKeys(card);
            return card;
        }
        catch { return null; }
    }

    /// <summary>
    /// 手动改卡名：把该角色的卡文件前缀改掉，并在卡里写 _显示名（之后保存也沿用它）。
    /// 只动文件名前缀，<c>-{id}</c> 后缀原样保留，所以 ID 匹配不受影响。
    /// </summary>
    public static (bool Ok, string Msg) RenameCardFiles(string npcCfgId, string newName)
    {
        if (string.IsNullOrWhiteSpace(npcCfgId)) return (false, "缺少角色代码");
        var safe = SanitizeFileName(newName);
        if (string.IsNullOrWhiteSpace(safe)) return (false, "名字不能为空");

        var dir = System.IO.Path.GetDirectoryName(MemoryPaths.CardPath(npcCfgId)) ?? "";
        if (!System.IO.Directory.Exists(dir)) return (false, "卡目录不存在");

        var tag = "-" + npcCfgId;
        var legacy = npcCfgId + ".json";
        var renamed = 0;

        try
        {
            foreach (var f in System.IO.Directory.GetFiles(dir))
            {
                var fn = System.IO.Path.GetFileName(f);
                string newFn = null;

                var idx = fn.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = idx + tag.Length;
                    if (after == fn.Length || fn[after] == '.' || fn[after] == '-')
                        newFn = safe + fn.Substring(idx);   // {新名}-{id}... 保留后缀
                }
                else if (string.Equals(fn, legacy, StringComparison.OrdinalIgnoreCase))
                {
                    newFn = safe + "-" + npcCfgId + ".json";   // 旧格式 → 新格式
                }

                if (newFn == null) continue;
                var np = System.IO.Path.Combine(dir, newFn);
                if (string.Equals(f, np, StringComparison.OrdinalIgnoreCase)) continue;
                System.IO.File.Move(f, np, true);
                renamed++;
            }
        }
        catch (Exception e) { return (false, "改文件名失败：" + e.Message); }

        if (renamed == 0) return (false, "没找到这个角色的卡文件");

        // 写 _显示名：以后保存卡时不再被配置表真名覆盖回去；卡里的姓名也一起纠正
        try
        {
            var main = ResolvePath(npcCfgId);
            if (main != null)
            {
                var card = JsonNode.Parse(System.IO.File.ReadAllText(main)) as JsonObject;
                if (card != null)
                {
                    card["_显示名"] = safe;
                    if (card["人设"] is JsonObject ren) ren["姓名"] = safe;
                    System.IO.File.WriteAllText(main, card.ToJsonString(PrettyOpts), new UTF8Encoding(false));
                }
            }
        }
        catch { }

        return (true, $"已改 {renamed} 个文件（列表里会显示「{safe}」）");
    }

    /// <summary>
    /// 旧卡兼容：人设区的「姓名 🎭」这类带标记的 key，统一改回不带标记的名字。
    /// 🎭 只是控制台界面上的提示，不该写进卡里。
    /// </summary>
    private static void NormalizeRenKeys(JsonObject card)
    {
        try
        {
            if (card?["人设"] is not JsonObject ren) return;
            foreach (var key in new[] { "姓名", "身份", "性格", "善恶立场", "外貌穿着" })
            {
                var marked = key + " 🎭";
                if (!ren.ContainsKey(marked)) continue;
                var v = ren[marked];
                if (!ren.ContainsKey(key) || string.IsNullOrWhiteSpace(ren[key]?.ToString()))
                    ren[key] = v == null ? null : JsonNode.Parse(v.ToJsonString());
                ren.Remove(marked);
            }
        }
        catch { }
    }

    /// <summary>玩家手动改过的卡名（控制台「改卡名」写入）；没有返回空。</summary>
    public static string GetDisplayName(string npcCfgId)
    {
        try
        {
            var card = Load(npcCfgId);
            return card?["_显示名"]?.GetValue<string>() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>写一张卡（原子替换，先写 .tmp）。</summary>
    public static bool Save(string npcCfgId, JsonObject card)
    {
        if (string.IsNullOrEmpty(npcCfgId) || card == null) return false;
        try
        {
            // ★ 文件名用「角色名-角色代码」（2026-10-24 主人定）；猜不到名字就退回旧格式
            var name = GuessName(npcCfgId, card);
            var p = PathOf(npcCfgId, name);
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            // 如果之前存的是旧格式（{代码}.json），迁到新格式后删掉旧的
            var old = MemoryPaths.CardPath(npcCfgId);
            var migrate = !string.Equals(old, p, StringComparison.OrdinalIgnoreCase)
                          && System.IO.File.Exists(old);

            var tmp = p + ".tmp";
            System.IO.File.WriteAllText(tmp, card.ToJsonString(PrettyOpts), new UTF8Encoding(false));
            System.IO.File.Move(tmp, p, overwrite: true);
            if (migrate)
            {
                try { System.IO.File.Delete(old); } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    public static bool Exists(string npcCfgId)
    {
        try { return ResolvePath(npcCfgId) != null; }
        catch { return false; }
    }

    /// <summary>这张卡是否启用（读顶层「启用」字段；缺省视为启用，兼容老卡/迁移卡）。</summary>
    public static bool IsEnabled(JsonObject card)
    {
        if (card == null) return false;
        try
        {
            var n = card["启用"];
            if (n == null) return true;   // 老卡 / 迁移卡没这个字段，按启用处理
            return n.GetValue<bool>();
        }
        catch { return true; }
    }

    // ==================== 草稿机制（阶段 5-D）====================

    /// <summary>草稿文件：{card}.优化中（和 1.1.2 一致）</summary>
    public static string DraftPathOf(string npcCfgId)
    {
        // 草稿跟着正式卡走：{名字}-{代码}.json.优化中
        var resolved = ResolvePath(npcCfgId);
        if (resolved != null) return resolved + ".优化中";
        return MemoryPaths.CardPath(npcCfgId) + ".优化中";
    }

    /// <summary>保存草稿（优化结果先落这里，玩家确认后才覆盖正式卡）</summary>
    public static bool SaveDraft(string npcCfgId, JsonObject card)
    {
        if (string.IsNullOrEmpty(npcCfgId) || card == null) return false;
        try
        {
            var p = DraftPathOf(npcCfgId);
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(p, card.ToJsonString(PrettyOpts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    public static JsonObject LoadDraft(string npcCfgId)
    {
        try
        {
            var p = DraftPathOf(npcCfgId);
            if (!System.IO.File.Exists(p)) return null;
            return JsonNode.Parse(System.IO.File.ReadAllText(p, new UTF8Encoding(false))) as JsonObject;
        }
        catch { return null; }
    }

    public static bool HasDraft(string npcCfgId)
    {
        try { return System.IO.File.Exists(DraftPathOf(npcCfgId)); }
        catch { return false; }
    }

    /// <summary>应用草稿：先备份正式卡，再用草稿覆盖，最后删草稿</summary>
    public static bool ApplyDraft(string npcCfgId, out string msg)
    {
        msg = "";
        try
        {
            var draft = LoadDraft(npcCfgId);
            if (draft == null) { msg = "没有待应用的优化"; return false; }

            // 备份正式卡
            var official = ResolvePath(npcCfgId) ?? PathOf(npcCfgId);
            if (System.IO.File.Exists(official))
            {
                try
                {
                    System.IO.File.Copy(official,
                        official + ".优化前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                        overwrite: true);
                }
                catch { }
            }

            if (!Save(npcCfgId, draft)) { msg = "写入正式卡失败"; return false; }
            DiscardDraft(npcCfgId);
            msg = "已应用优化（旧卡已备份）";
            return true;
        }
        catch (Exception e) { msg = "应用失败：" + e.Message; return false; }
    }

    public static bool DiscardDraft(string npcCfgId)
    {
        try
        {
            var p = DraftPathOf(npcCfgId);
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取一张「可注入」的卡：存在 + 启用 + 全局开关打开，三者全满足才返回，否则 null。
    /// 调用方（tail 注入）只需要判断返回值。
    /// </summary>
    public static JsonObject LoadForInject(string npcCfgId)
    {
        if (!PromptConfig.CardEnabled) return null;      // 全局开关（config.json 的 cardEnabled）
        var card = Load(npcCfgId);
        if (card == null) return null;
        if (!IsEnabled(card)) return null;
        return card;
    }
}
