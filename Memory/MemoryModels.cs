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

/// <summary>骨架：关系段（append-only）+ 处境段（随剧情替换）</summary>
public sealed class Skeleton
{
    /// <summary>关系条目，只增不减（玩家手动删除外）。如「玩家是她的徒弟」</summary>
    public List<string> Relations { get; set; } = new();

    /// <summary>当前处境，随剧情替换。如「正在追查一桩亏空」</summary>
    public string Situation { get; set; } = "";
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
