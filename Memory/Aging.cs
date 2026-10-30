namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆老化 / 淘汰（5.2 ~ 5.5）。
/// 机制定死，阈值参数化（可在控制台调）。
/// </summary>
public static class Aging
{
    /// <summary>降级阈值：age 达到该值且 importance>1 时，importance-1、age 归零</summary>
    public static int DemoteAgeThreshold = 3;

    /// <summary>删除阈值：importance==1 且 age 达到该值时删除</summary>
    public static int DeleteAgeThreshold = 6;

    /// <summary>4 级软上限</summary>
    public static int Level4Cap = 20;

    /// <summary>dialog chunk 容量上限</summary>
    public static int DialogChunkCap = 2000;

    /// <summary>
    /// 5.2 事实老化。每次抽取跑一次，只作用于 1~3 级（4~5 级、关系里程碑永不老化）。
    /// 命中重置 age 的逻辑由阶段 3 检索接入（调用前把命中的 fact 的 Age 归零）。
    /// 返回本次发生的遗忘描述（写日志用）。
    /// </summary>
    public static List<string> AgeFacts(NpcMemory mem, string npcCfgId)
    {
        var events = new List<string>();
        foreach (var f in mem.Facts.ToList())
        {
            // 4~5 级、关系里程碑不老化
            if (f.Importance >= 4 || f.Category == FactCategory.RelationMilestone)
                continue;

            f.Age += 1;

            if (f.Age >= DemoteAgeThreshold && f.Importance > 1)
            {
                f.Importance -= 1;
                f.Age = 0;
                var msg = $"降级到 {f.Importance} 级 「{f.Text}」";
                events.Add(msg);
                MemoryStore.LogForget(npcCfgId, "fact 老化降级", msg);
            }

            if (f.Importance == 1 && f.Age >= DeleteAgeThreshold)
            {
                mem.Facts.Remove(f);
                var msg = $"老化删除 「{f.Text}」";
                events.Add(msg);
                MemoryStore.LogForget(npcCfgId, "fact 老化删除", msg);
            }
        }
        return events;
    }

    /// <summary>5.3 4 级软上限：超限按「最久未提及（Age 最大）」降级到 3 级。</summary>
    public static void EnforceLevel4Cap(NpcMemory mem, string npcCfgId)
    {
        var level4 = mem.Facts
            .Where(f => f.Importance == 4)
            .OrderByDescending(f => f.Age)
            .ToList();
        var overflow = level4.Skip(Level4Cap).ToList();
        foreach (var f in overflow)
        {
            f.Importance = 3;
            f.Age = 0;
            MemoryStore.LogForget(npcCfgId, "4级配额降级", f.Text);
        }
    }

    /// <summary>
    /// 5.5 dialog chunk 容量淘汰：超 2000 块时，按「最久未召回 + 轮次最早」淘汰。
    /// summary 块永不淘汰。
    /// </summary>
    public static void EvictDialogChunks(NpcMemory mem, string npcCfgId)
    {
        var dialogs = mem.Chunks.Where(c => c.Kind == ChunkKind.Dialog).ToList();
        if (dialogs.Count <= DialogChunkCap) return;

        var toRemove = dialogs
            .OrderBy(c => c.LastSeen)
            .ThenBy(c => c.Turn)
            .Take(dialogs.Count - DialogChunkCap)
            .ToList();

        foreach (var c in toRemove)
        {
            mem.Chunks.Remove(c);
            var brief = c.Text.Length > 40 ? c.Text[..40] : c.Text;
            MemoryStore.LogForget(npcCfgId, "chunk 容量淘汰", brief);
        }
    }

    /// <summary>dialog chunk 的时间衰减系数（检索时加权用，5.5）。</summary>
    public static float TimeDecay(Chunk c, long nowEpochSeconds)
    {
        var days = (nowEpochSeconds - c.LastSeen) / 86400.0;
        if (days <= 30) return 1.0f;
        if (days <= 90) return 0.9f;
        if (days <= 180) return 0.8f;
        return 0.7f;
    }
}
