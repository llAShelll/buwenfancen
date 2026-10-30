using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆维护工具（阶段 4-C 一键迁移）。
///
/// ① 重评重要度：用 V1.2 的收紧标准重新给 facts 打分
///    （1.1.2 旧标准很宽，实测某角色 179 条事实里 57% 是 4 级 → 注入权重失真）
/// ② 压缩摘要：把最早的一批 dialog chunk 交给模型合成摘要句，替换成 summary chunk
///
/// 两个操作都会先备份原文件，失败不动数据。
/// </summary>
public static class MemoryTools
{
    // ==================== ① 重评重要度 ====================

    private const string ReclassifySystem = @"你是记忆重要度评审员。下面是一行一条的【已有记忆事实】，格式：索引|旧等级|内容。
请按【新的重要度标准】重新评估每一条的 importance，只输出 JSON。

【新标准】
  1 = 琐事：一次寒暄、一句闲话、一个普通动作
  2 = 一般：日常小约定、普通见闻
  3 = 重要：有意义的事、情节推进、关系小进展、关心的话
  4 = 很重要 —— ★ 只有下面四种才给 4，其余一律 3 或以下：
        · 影响关系走向的重大承诺（提亲、结契、生死之约）
        · 关键剧情转折（身份揭示、重大冲突、重要抉择）
        · 付出重大代价的事
        · 明确的关系升级
  5 = 刻骨铭心：结契、拜师、名分、生死相托

【铁律】
  · 日常小约定（递水、收东西、明日几点来、吃饭穿衣之类）最多给 3，绝不要给 4。
  · 只有【影响关系走向】的承诺才给 4。
  · 赠物类：普通物件 2~3；有特殊意义（定情、家族信物）才 3~4。
  · 事件类：按「对剧情/关系的影响」打分，不是按篇幅打分。
  · ★ 不许改写内容、不许合并、不许删除；只输出索引和新的 importance。

【输出格式】（严格 JSON，不要多余文字）
{""评级"":[{""id"":0,""importance"":3},{""id"":1,""importance"":2}]}";

    public static async Task<(bool Ok, string Msg)> ReclassifyAsync(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return (false, "缺少角色 ID");

        NpcMemory mem;
        try { mem = MemoryStore.Load(npcCfgId); }
        catch (Exception e) { return (false, "读记忆失败：" + e.Message); }

        if (mem.Facts.Count == 0) return (false, "这个角色还没有关键事实，不用重评");

        var cfg = MemoryEngine.GetLlmConfig(null);   // 只走 settings.json（控制台没有 runtime 对象）
        if (!cfg.IsValid) return (false, "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey");

        // 组装输入
        var sb = new StringBuilder();
        for (var i = 0; i < mem.Facts.Count; i++)
            sb.AppendLine($"{i}|{mem.Facts[i].Importance}|{mem.Facts[i].Text}");

        var (content, err) = await LlmClient.CompleteAsync(cfg, ReclassifySystem, sb.ToString(), 4000);
        if (content == null) return (false, "模型调用失败：" + err);

        // 解析 {"评级":[{"id":0,"importance":3}...]}
        var applied = 0;
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return (false, "模型返回不是 JSON：" + Truncate(content, 120));
            var json = content.Substring(start, end - start + 1);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("评级", out var arr))
                return (false, "模型返回缺「评级」字段：" + Truncate(content, 120));

            var before = new int[mem.Facts.Count];
            for (var i = 0; i < mem.Facts.Count; i++) before[i] = mem.Facts[i].Importance;

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                if (!item.TryGetProperty("importance", out var impEl)) continue;
                var id = idEl.GetInt32();
                var imp = impEl.GetInt32();
                if (id < 0 || id >= mem.Facts.Count) continue;
                if (imp < 1 || imp > 5) continue;
                mem.Facts[id].Importance = imp;
                mem.Facts[id].Age = 0;   // 重评后年龄归零（重新计时）
                applied++;
            }

            // 统计变化
            var up = 0; var down = 0;
            for (var i = 0; i < mem.Facts.Count; i++)
            {
                if (mem.Facts[i].Importance > before[i]) up++;
                else if (mem.Facts[i].Importance < before[i]) down++;
            }

            if (applied == 0) return (false, "模型没返回任何有效评级");

            // 备份 + 保存
            BackupFacts(npcCfgId);
            MemoryStore.Save(mem);
            MemoryEngine.InvalidateCache(npcCfgId);
            MemoryRetriever.Invalidate(npcCfgId);

            return (true, $"已重评 {applied} 条事实（升 {up} 条 / 降 {down} 条 / 不变 {applied - up - down} 条）");
        }
        catch (Exception e)
        {
            return (false, "解析或保存失败：" + e.Message);
        }
    }

    // ==================== ② 压缩摘要 ====================

    private const string CompressSystem = @"你是记忆摘要员。下面是一段【较早的对话原文块】。
请把它压缩成若干条摘要句，只输出正文，一行一条，不要编号、不要标题、不要解释。

硬性要求：
- 每条不超过 60 字，总共不超过 12 条
- 必须保留：剧情主线、人物关系的变化、关键承诺与约定、当前状态与最近时间地点
- 可以删掉：重复的描写、次要的对话细节、大段情绪铺陈
- 用第三人称、连贯叙述（不要写成条目清单的格式，就是自然的句子）";

    /// <summary>
    /// 把最早的 dialog chunk 压缩成 summary。
    /// ratio 默认 0.5：只压最早的一半，留最近一半原文。
    /// </summary>
    public static async Task<(bool Ok, string Msg)> CompressAsync(string npcCfgId, double ratio = 0.5)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return (false, "缺少角色 ID");

        NpcMemory mem;
        try { mem = MemoryStore.Load(npcCfgId); }
        catch (Exception e) { return (false, "读记忆失败：" + e.Message); }

        var dialogs = mem.Chunks
            .Where(c => c.Kind == ChunkKind.Dialog)
            .OrderBy(c => c.Turn)
            .ToList();

        if (dialogs.Count < 20)
            return (false, $"对话框只有 {dialogs.Count} 条，太少，不需要压缩");

        var takeCount = Math.Max(10, (int)(dialogs.Count * Math.Clamp(ratio, 0.2, 0.8)));
        var targets = dialogs.Take(takeCount).ToList();

        var cfg = MemoryEngine.GetLlmConfig(null);
        if (!cfg.IsValid) return (false, "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey");

        var text = string.Join("\n", targets.Select(c => c.Text));
        if (text.Length > 24000) text = text.Substring(0, 24000);   // 防超长

        var (content, err) = await LlmClient.CompleteAsync(cfg, CompressSystem, text, 2500);
        if (content == null) return (false, "模型调用失败：" + err);

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d+[\.\、\)]\s*", ""))  // 去掉可能残留的序号
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return (false, "模型没返回有效摘要");

        // 备份 + 替换
        BackupChunks(npcCfgId);

        // ① 先删掉被压缩的 dialog
        var targetIds = targets.Select(c => c.Id).ToHashSet();
        mem.Chunks = mem.Chunks.Where(c => !targetIds.Contains(c.Id)).ToList();
        foreach (var id in targetIds) mem.Vectors.Remove(id);

        // ② 追加 summary（ReplaceSummaryChunks 会重写 chunks.jsonl + 落盘向量）
        var added = MemoryStore.ReplaceSummaryChunks(mem, lines, 1500);

        // ③ 删掉 dialog 之后的文件要再落一次盘（ReplaceSummaryChunks 已经重写了 chunks.jsonl，
        //    但 facts/meta 没动，这里只需作废缓存）
        MemoryEngine.InvalidateCache(npcCfgId);
        MemoryRetriever.Invalidate(npcCfgId);

        return (true, $"已压缩 {takeCount} 条对话 → {added} 条摘要（原文 {text.Length} 字 → 摘要 {lines.Sum(l => l.Length)} 字）");
    }

    // ==================== 工具 ====================

    private static void BackupFacts(string npcCfgId)
    {
        try
        {
            var dir = MemoryPaths.MemDir(npcCfgId);
            var src = System.IO.Path.Combine(dir, "facts.json");
            if (System.IO.File.Exists(src))
                System.IO.File.Copy(src,
                    System.IO.Path.Combine(dir, "facts.重评前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"),
                    overwrite: true);
        }
        catch { }
    }

    private static void BackupChunks(string npcCfgId)
    {
        try
        {
            var dir = MemoryPaths.MemDir(npcCfgId);
            var src = System.IO.Path.Combine(dir, "chunks.jsonl");
            if (System.IO.File.Exists(src))
                System.IO.File.Copy(src,
                    System.IO.Path.Combine(dir, "chunks.压缩前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jsonl"),
                    overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// 手动完整抽取（2026-10-27 主人要：「立即整理」按钮用）。
    /// 把当前所有未整理的对话提炼成【摘要 + facts + 骨架】，同步等它跑完。
    /// </summary>
    public static (bool Ok, string Msg) Extract(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return (false, "缺少角色 ID");
        try
        {
            var mem = MemoryStore.Load(npcCfgId);
            if (mem == null) return (false, "没有这个角色的记忆");
            var cfg = MemoryEngine.GetLlmConfig(null);
            if (cfg == null || !cfg.IsValid) return (false, "读不到 API 配置（settings.json 的 ainpc 段缺 url 或 apiKey）");
            return Extractor.ExtractNow(mem, cfg);
        }
        catch (Exception e)
        {
            return (false, "整理失败：" + e.Message);
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
