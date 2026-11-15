using System.Text.Json.Serialization;

namespace BuWenFanChen.Memory;

/// <summary>
/// 阶段 2 · 记忆引擎数据模型（四层：骨架 / chunk / facts / meta）。
/// ★ 全部用标准 .NET 类型（string/List/int），不碰 Il2Cpp 类型，
///   这样 System.Text.Json 能直接序列化，且不依赖游戏运行时。
/// </summary>

/// <summary>fact 的四大类（固定，不扩展 ✗ 世界观走 chunk 的 kind）</summary>
public enum FactCategory
{
    Gift,               // 赠物
    Promise,            // 承诺
    RelationMilestone,  // 关系里程碑
    ImportantEvent      // 重要事件
}

/// <summary>chunk 的种类（开放枚举，为世界观预留）</summary>
public sealed class ChunkKind
{
    public const string Dialog = "dialog";
    public const string Summary = "summary";
    public const string Worldbook = "worldbook";   // 预留，现在不产生
}

/// <summary>fact 来源（api = 可信；none = 没生成，靠 chunk 兜）</summary>
public sealed class FactSource
{
    public const string Api = "api";
    public const string None = "none";
}

/// <summary>总述的一「代」：每次记忆合并（跟档位走，12~48 轮）批量产出 1~4 代，每代 ≤30 字，只写事件来龙去脉、不写描写。</summary>
public sealed class PastGen
{
    /// <summary>代数（1,2,3…递增；第 0 代是迁移来的旧总述）</summary>
    public int Gen { get; set; }
    /// <summary>这一代的内容（≤30 字）</summary>
    public string Text { get; set; } = "";
    /// <summary>生成时的对话轮次</summary>
    public long Turn { get; set; }
}

/// <summary>骨架：关系段（append-only）+ 过往总述段（分代累积）+ 处境段（随剧情替换）</summary>
public sealed class Skeleton
{
    /// <summary>过往总述的字数上限（字符数）。★ 2026-11-07：现在是「渲染总字数」上限，不是单代上限。</summary>
    public const int MaxPastSummaryChars = 300;

    /// <summary>★ 2026-11-07 主人定：最多保留几代（30 字 × 10 代 = 300 字）</summary>
    public const int MaxPastGens = 10;

    /// <summary>★ 2026-11-07 主人定：每代字数上限（只写事件，不写描写）</summary>
    public const int GenCharLimit = 30;

    /// <summary>关系条目，只增不减（玩家手动删除外）。如「玩家是她的徒弟」</summary>
    public List<string> Relations { get; set; } = new();

    /// <summary>
    /// 旧字段：单段总述（**兼容旧数据**）。
    /// ★ 2026-11-07 起新逻辑用 <see cref="PastGens"/>；这个字段只在迁移时读写，
    ///   以及控制台手动编辑时兜底。
    /// </summary>
    public string PastSummary { get; set; } = "";

    /// <summary>
    /// ★ 2026-11-07 新：分代总述。
    /// 每次记忆合并（12~48 轮，跟上下文档位的合并节拍走）批量产出 1~4 代（每代 ≤30 字，只写事件）；保留最近 10 代，超了淘汰最老。
    /// 为什么改：原来「整体替换 300 字」，模型经常只写最近的事 → 旧事被覆盖。
    /// </summary>
    public List<PastGen> PastGens { get; set; } = new();

    /// <summary>当前处境，随剧情替换。如「正在追查一桩亏空」</summary>
    public string Situation { get; set; } = "";

    /// <summary>
    /// 渲染给提示词用的总述：从**新到旧**凑满 maxChars（最新的一定保留，最老的先被挤）。
    /// 有 PastGens 就用它；没有就回落旧字段 PastSummary（迁移前的数据）。
    /// </summary>
    public string RenderPastSummary(int maxChars = MaxPastSummaryChars)
    {
        try
        {
            if (PastGens == null || PastGens.Count == 0)
                return (PastSummary ?? "").Trim();

            var parts = new List<string>();
            int used = 0;
            for (int i = PastGens.Count - 1; i >= 0; i--)
            {
                var g = PastGens[i];
                if (g == null || string.IsNullOrWhiteSpace(g.Text)) continue;
                var t = g.Text.Trim();
                // 前面已经有内容、再加就超了 → 停（保证最新的一定在）
                if (parts.Count > 0 && used + t.Length + 1 > maxChars) break;
                parts.Insert(0, t);
                used += t.Length + 1;
            }
            return string.Join("\n", parts);
        }
        catch { return (PastSummary ?? "").Trim(); }
    }

    /// <summary>
    /// 追加一代。返回新增的那代；evicted 是被淘汰的最老一代（没有则 null）。
    /// 硬截断到 <see cref="GenCharLimit"/> 字。
    /// </summary>
    public PastGen AddPastGen(string text, long turn, out PastGen evicted)
    {
        evicted = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > GenCharLimit) t = t.Substring(0, GenCharLimit);

        if (PastGens == null) PastGens = new List<PastGen>();
        var nextGen = PastGens.Count > 0 ? PastGens[PastGens.Count - 1].Gen + 1 : 1;
        var gen = new PastGen { Gen = nextGen, Text = t, Turn = turn };
        PastGens.Add(gen);

        while (PastGens.Count > MaxPastGens)
        {
            evicted = PastGens[0];
            PastGens.RemoveAt(0);
        }
        return gen;
    }
}

/// <summary>一条记忆块（原文块 dialog / 摘要句 summary / 世界观 worldbook）</summary>
public sealed class Chunk
{
    public int Id { get; set; }
    public string Kind { get; set; } = ChunkKind.Dialog;
    public string Npc { get; set; } = "";                 // 归属角色（必填）
    public List<string> Witness { get; set; } = new();    // 同场角色（现在永远空）
    public string Text { get; set; } = "";
    public int Turn { get; set; }                          // 产生时的轮次
    public long CreatedAt { get; set; }                    // 创建时间戳（epoch 秒）
    public long LastSeen { get; set; }                     // 最近被召回时间（衰减用）
    /// <summary>
    /// 最近被召回的轮次（★ 2026-11-03：老化按轮数，不按系统时间——单机游戏可能几个月不玩）。
    /// 0 = 未知/迁移数据，不参与轮数老化。
    /// </summary>
    public int LastSeenTurn { get; set; }
}

/// <summary>一条关键事实（4 类 × importance 1~5 × age）</summary>
public sealed class Fact
{
    public FactCategory Category { get; set; }
    public string Text { get; set; } = "";
    public int Importance { get; set; } = 1;               // 1~5
    public int Age { get; set; }                           // 合并次数（老化依据）
    public string Span { get; set; } = "";                 // 原文片段（4 级校验用）
    public string Npc { get; set; } = "";                  // 归属
    public List<string> Witness { get; set; } = new();
    public string Source { get; set; } = FactSource.Api;    // api / none
}

/// <summary>每个角色的记忆元数据（meta.json）</summary>
public sealed class MemoryMeta
{
    public int SchemaVersion { get; set; } = 1;
    public string Scope { get; set; } = "";                // 存档适配占位，默认空
    public int ExportVersion { get; set; } = 1;            // 导出/导入兼容
    public string NpcCfgId { get; set; } = "";             // 稳定 ID（落盘目录名）
    public string NpcName { get; set; } = "";              // 显示名（会随皮肤变）
    public string NpcTitle { get; set; } = "";
    public int LastExtractTurn { get; set; }               // 上次抽取的轮次（防重复）
}

/// <summary>一个角色的完整记忆（内存态）</summary>
public sealed class NpcMemory
{
    public MemoryMeta Meta { get; set; } = new();
    public Skeleton Skeleton { get; set; } = new();
    public List<Chunk> Chunks { get; set; } = new();
    public List<Fact> Facts { get; set; } = new();

    /// <summary>chunk 的 BGE 向量索引（不参与 JSON，单独落盘 chunks.vec）</summary>
    [JsonIgnore]
    public VectorIndex Vectors { get; set; } = new();

    public int NextChunkId { get; set; } = 1;              // 自增 id（落盘时存进 meta）
}
