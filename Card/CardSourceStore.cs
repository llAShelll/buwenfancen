using System.Text;

namespace BuWenFanChen.Card;

/// <summary>
/// 角色原始资料仓库（阶段 5-B 扩展：自动抓取）。
///
/// 游戏每次调 LLM 的 prefix 里都带着这段：
///   ## 角色属性（身份/性格/外貌/善恶）
///   ## 角色设定（你是 XXX）
///   ## 角色背景
///   ## 说话习惯
///
/// 我们在处理 prefix 时顺手截出来，存成 card\{npcCfgId}.source.md。
/// 控制台「角色卡优化」页直接读这份资料去生成卡片，玩家不用手动粘贴。
///
/// 好处：资料跟着游戏走，游戏更新角色设定后，下次进对话自动刷新。
/// </summary>
public static class CardSourceStore
{
    /// <summary>
    /// 资料文件路径：{card 目录}\{角色名}-{npcCfgId}.source.md
    /// 猜不到名字时退回 {npcCfgId}.source.md（兼容旧文件）。
    /// </summary>
    public static string PathOf(string npcCfgId)
    {
        var cardJson = Memory.MemoryPaths.CardPath(npcCfgId);
        var dir = System.IO.Path.GetDirectoryName(cardJson) ?? ".";

        // 从 meta.json 读角色名
        // ★ 2026-10-25：优先配置表初始名（meta.json 里可能是皮肤名）
        var name = "";
        try { name = Memory.ConfigTable.GetBaseName(npcCfgId); } catch { }
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var metaPath = System.IO.Path.Combine(Memory.MemoryPaths.MemDir(npcCfgId), "meta.json");
                if (System.IO.File.Exists(metaPath))
                {
                    var meta = System.Text.Json.Nodes.JsonNode.Parse(
                        System.IO.File.ReadAllText(metaPath, new UTF8Encoding(false)));
                    name = meta?["NpcName"]?.GetValue<string>() ?? "";
                }
            }
            catch { }
        }

        var safe = CardStore.SanitizeFileName(name);
        if (string.IsNullOrEmpty(safe))
            return System.IO.Path.Combine(dir, npcCfgId + ".source.md");
        return System.IO.Path.Combine(dir, safe + "-" + npcCfgId + ".source.md");
    }

    public static void Save(string npcCfgId, string material)
    {
        if (string.IsNullOrEmpty(npcCfgId) || string.IsNullOrWhiteSpace(material)) return;
        try
        {
            var p = PathOf(npcCfgId);
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            // 内容没变就不写盘（这个函数每轮都会被调用，避免无谓 IO）
            if (System.IO.File.Exists(p))
            {
                try
                {
                    var old = System.IO.File.ReadAllText(p, new UTF8Encoding(false));
                    if (old == material) return;
                }
                catch { }
            }

            System.IO.File.WriteAllText(p, material, new UTF8Encoding(false));
        }
        catch { }
    }

    public static string Load(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return null;
        try
        {
            var p = PathOf(npcCfgId);
            // 兼容旧格式：{代码}.source.md
            if (!System.IO.File.Exists(p))
            {
                var dir = System.IO.Path.GetDirectoryName(p) ?? ".";
                var old = System.IO.Path.Combine(dir, npcCfgId + ".source.md");
                if (System.IO.File.Exists(old)) p = old;
                else return null;
            }
            var t = System.IO.File.ReadAllText(p, new UTF8Encoding(false));
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    public static bool Exists(string npcCfgId)
    {
        try { return System.IO.File.Exists(PathOf(npcCfgId)); }
        catch { return false; }
    }

    /// <summary>
    /// 从抓到的角色资料里提取【真名】—— game prefix 里的「## 角色设定 → 你是 XXX（头衔）」。
    /// ★ 2026-10-25 探针实测：配置表里只有皮肤名（「某某·某角色」），
    ///   但游戏 prefix 的「你是 某某（某堂执事弟子）」里是真名 —— 这是最可靠的来源。
    /// </summary>
    public static string ExtractRealName(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return "";
        // ① 优先「## 角色设定」段里的「你是 XXX」
        var m = System.Text.RegularExpressions.Regex.Match(material,
            @"##\s*角色设定[^\n]*\n+\s*你是\s*([^（(\n，。、\s]+)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success) return m.Groups[1].Value.Trim();
        // ② 兜底：直接找「你是 XXX」
        m = System.Text.RegularExpressions.Regex.Match(material,
            @"你是\s*([^（(\n，。、\s]+)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success) return m.Groups[1].Value.Trim();
        return "";
    }

    /// <summary>
    /// 从游戏 prefix 里截出「角色资料」那几段。
    /// 起点是「## 角色属性」，终点是「## 说话习惯」段结束（或者在下一个「## 」之前）。
    /// 截不出来返回 null（不写盘）。
    /// </summary>
    public static string ExtractFromPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;

        var start = prefix.IndexOf("## 角色属性", StringComparison.Ordinal);
        if (start < 0)
        {
            // 有的角色可能没有「角色属性」段，退一步用「## 角色设定」
            start = prefix.IndexOf("## 角色设定", StringComparison.Ordinal);
        }
        if (start < 0) return null;

        var speak = prefix.IndexOf("## 说话习惯", start, StringComparison.Ordinal);
        int end;
        if (speak >= 0)
        {
            // ★ 2026-10-27：把「## 说话习惯」也抓进来（终点 = 这段结束，即下一个「\n## 」之前）。
            //   之前 end = speak（停在段首），source.md 永远没有「说话习惯」，
            //   AI 写卡只能从「性格」瞎猜口吻 → 把「认真拘谨」猜成「懒散随性」，角色直接变一个人。
            var nextAfterSpeak = prefix.IndexOf("\n## ", speak + 6, StringComparison.Ordinal);
            end = nextAfterSpeak >= 0 ? nextAfterSpeak : prefix.Length;
        }
        else
        {
            var bg = prefix.IndexOf("## 角色背景", start, StringComparison.Ordinal);
            if (bg >= 0)
            {
                var nextAfterBg = prefix.IndexOf("\n## ", bg + 6, StringComparison.Ordinal);
                end = nextAfterBg >= 0 ? nextAfterBg : prefix.Length;
            }
            else
            {
                var next = prefix.IndexOf("\n## ", start + 6, StringComparison.Ordinal);
                end = next >= 0 ? next : prefix.Length;
            }
        }

        if (end <= start) return null;
        var material = prefix.Substring(start, end - start).Trim();
        // 太短说明没截到真东西
        return material.Length >= 40 ? material : null;
    }
}
