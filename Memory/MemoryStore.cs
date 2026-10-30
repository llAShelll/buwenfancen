using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆落盘 / 读盘。文件结构：
///   {MemDir}\skeleton.txt    骨架（文本）
///   {MemDir}\chunks.jsonl    每行一个 chunk（JSON Lines，增量追加）
///   {MemDir}\facts.json      facts 数组
///   {MemDir}\meta.json       元数据
///   {MemDir}\遗忘纪录.log    遗忘/删除记录（追加）
///   {MemDir}\备份\           历史备份
/// 所有写操作先写 .tmp 再 replace，避免进程中断损坏文件。
/// </summary>
public static class MemoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions JsonOptsPretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // ================= 加载 =================

    /// <summary>加载一个角色的完整记忆；目录不存在则返回空的 NpcMemory。</summary>
    public static NpcMemory Load(string npcCfgId)
    {
        var mem = new NpcMemory();
        mem.Meta.NpcCfgId = npcCfgId;   // ★ 无论如何都先设置，目录不存在也不丢 ID
        var dir = MemoryPaths.MemDir(npcCfgId);
        if (!System.IO.Directory.Exists(dir))
            return mem;

        // meta.json
        var metaPath = System.IO.Path.Combine(dir, "meta.json");
        if (System.IO.File.Exists(metaPath))
        {
            try { mem.Meta = JsonSerializer.Deserialize<MemoryMeta>(ReadText(metaPath), JsonOpts) ?? new(); }
            catch { mem.Meta = new(); }
        }
        mem.Meta.NpcCfgId = npcCfgId;

        // skeleton.txt
        var skPath = System.IO.Path.Combine(dir, "skeleton.txt");
        if (System.IO.File.Exists(skPath))
        {
            try { mem.Skeleton = ParseSkeleton(ReadText(skPath)); }
            catch { mem.Skeleton = new(); }
        }

        // chunks.jsonl（增量文件，逐行读）
        var chunkPath = System.IO.Path.Combine(dir, "chunks.jsonl");
        if (System.IO.File.Exists(chunkPath))
        {
            var maxId = 0;
            foreach (var line in ReadLines(chunkPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var c = JsonSerializer.Deserialize<Chunk>(line, JsonOpts);
                    if (c != null)
                    {
                        mem.Chunks.Add(c);
                        if (c.Id > maxId) maxId = c.Id;
                    }
                }
                catch { /* 跳过坏行 */ }
            }
            mem.NextChunkId = maxId + 1;

            // ★ 2026-10-26：老数据补轮次。
            //   游戏传的 promptTurnId 常为 0 → 控制台「对话原文」全是「第 0 轮」。
            //   这里按出现顺序给 dialog 补 1..N（幂等，只补 <=0 的），新数据由 WriteTurn 自增。
            var seqNo = 0;
            foreach (var c in mem.Chunks)
            {
                if (c.Kind != ChunkKind.Dialog) continue;
                seqNo++;
                if (c.Turn <= 0) c.Turn = seqNo;
            }
        }

        // facts.json
        var factPath = System.IO.Path.Combine(dir, "facts.json");
        if (System.IO.File.Exists(factPath))
        {
            try { mem.Facts = JsonSerializer.Deserialize<List<Fact>>(ReadText(factPath), JsonOpts) ?? new(); }
            catch { mem.Facts = new(); }
        }

        // chunks.vec（BGE 向量索引）
        var vecPath = MemoryPaths.VectorPath(npcCfgId);
        mem.Vectors = VectorIndex.Load(vecPath);

        // 补齐缺失的向量（新写入的 chunk / 换模型后）
        if (EmbedderProvider.IsReady && mem.Chunks.Count > 0)
        {
            var need = mem.Chunks.Where(c => !mem.Vectors.Contains(c.Id)).ToList();
            if (need.Count > 0)
            {
                foreach (var c in need)
                {
                    var v = EmbedderProvider.Embed(c.Text ?? "");
                    if (v != null) mem.Vectors.Add(c.Id, v);
                }
                try { mem.Vectors.Save(vecPath); } catch { }
            }
        }

        return mem;
    }

    // ================= 保存 =================

    /// <summary>全量保存（meta + skeleton + facts）。chunks 走增量，不在这里重写。</summary>
    public static void Save(NpcMemory mem)
    {
        var dir = MemoryPaths.MemDir(mem.Meta.NpcCfgId);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "备份"));

        // meta.json
        WriteTextAtomic(System.IO.Path.Combine(dir, "meta.json"),
            JsonSerializer.Serialize(mem.Meta, JsonOptsPretty));

        // skeleton.txt
        WriteTextAtomic(System.IO.Path.Combine(dir, "skeleton.txt"), RenderSkeleton(mem.Skeleton));

        // facts.json
        WriteTextAtomic(System.IO.Path.Combine(dir, "facts.json"),
            JsonSerializer.Serialize(mem.Facts, JsonOptsPretty));
    }

    /// <summary>追加一个 chunk（增量写，不重写整个文件），顺便算它的 BGE 向量。</summary>
    public static void AppendChunk(NpcMemory mem, Chunk chunk)
    {
        if (chunk.Id <= 0) chunk.Id = mem.NextChunkId++;
        var dir = MemoryPaths.MemDir(mem.Meta.NpcCfgId);
        var path = System.IO.Path.Combine(dir, "chunks.jsonl");
        System.IO.Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(chunk, JsonOpts);
        System.IO.File.AppendAllText(path, line + "\n", new System.Text.UTF8Encoding(false));
        mem.Chunks.Add(chunk);

        // ★ 阶段 3②：算向量并落盘
        if (!mem.Vectors.Contains(chunk.Id))
        {
            var v = EmbedderProvider.Embed(chunk.Text ?? "");
            if (v != null)
            {
                mem.Vectors.Add(chunk.Id, v);
                try { mem.Vectors.Save(MemoryPaths.VectorPath(mem.Meta.NpcCfgId)); } catch { }
            }
        }
    }

    /// <summary>
    /// 替换所有摘要块（摘要是对历史的总述，每次重新生成，不是增量追加）。
    /// · 剔掉旧 Summary chunk → 追加新摘要 → 重写 chunks.jsonl → 同步向量
    /// </summary>
    public static int ReplaceSummaryChunks(NpcMemory mem, List<string> summaries, int maxChars = 500)
    {
        if (mem == null || summaries == null) return 0;

        var dir = MemoryPaths.MemDir(mem.Meta.NpcCfgId);
        System.IO.Directory.CreateDirectory(dir);

        // ① 剔掉旧摘要（保留 dialog）
        var oldSummaryIds = mem.Chunks.Where(c => c.Kind == ChunkKind.Summary).Select(c => c.Id).ToList();
        if (oldSummaryIds.Count > 0)
        {
            mem.Chunks = mem.Chunks.Where(c => c.Kind != ChunkKind.Summary).ToList();
            foreach (var id in oldSummaryIds) mem.Vectors.Remove(id);
        }

        // ② 追加新摘要
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var turn = mem.Chunks.Count > 0 ? mem.Chunks[^1].Turn + 1 : 1;
        var totalChars = 0;
        var added = 0;
        foreach (var sentence in summaries)
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            if (totalChars + sentence.Length > maxChars) break;

            var chunk = new Chunk
            {
                Kind = ChunkKind.Summary,
                Npc = MemoryEngine.ResolveName(mem.Meta.NpcCfgId),
                Text = sentence.Trim(),
                Turn = turn,
                CreatedAt = now,
                LastSeen = now,
            };
            chunk.Id = mem.NextChunkId++;
            mem.Chunks.Add(chunk);
            totalChars += sentence.Length;
            added++;

            var v = EmbedderProvider.Embed(chunk.Text);
            if (v != null) mem.Vectors.Add(chunk.Id, v);
        }

        // ③ 全量重写 chunks.jsonl（摘要改动后必须重写，追加式文件无法删行）
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in mem.Chunks)
                sb.AppendLine(JsonSerializer.Serialize(c, JsonOpts));
            WriteTextAtomic(System.IO.Path.Combine(dir, "chunks.jsonl"), sb.ToString().TrimEnd('\n'));
        }
        catch { }

        // ④ 向量索引落盘
        try { mem.Vectors.Save(MemoryPaths.VectorPath(mem.Meta.NpcCfgId)); } catch { }

        return added;
    }

    /// <summary>改一条 chunk 的文字并重算向量（摘要编辑用）。</summary>
    public static bool UpdateChunkText(NpcMemory mem, int chunkId, string newText)
    {
        var c = mem.Chunks.Find(x => x.Id == chunkId);
        if (c == null) return false;
        c.Text = newText ?? "";
        var v = EmbedderProvider.Embed(c.Text);
        if (v != null) mem.Vectors.Add(c.Id, v);
        RewriteChunks(mem);
        try { mem.Vectors.Save(MemoryPaths.VectorPath(mem.Meta.NpcCfgId)); } catch { }
        return true;
    }

    /// <summary>删一条 chunk（连同向量），记遗忘日志。</summary>
    public static bool RemoveChunk(NpcMemory mem, int chunkId, string reason)
    {
        var c = mem.Chunks.Find(x => x.Id == chunkId);
        if (c == null) return false;
        mem.Chunks.Remove(c);
        mem.Vectors.Remove(chunkId);
        RewriteChunks(mem);
        try { mem.Vectors.Save(MemoryPaths.VectorPath(mem.Meta.NpcCfgId)); } catch { }
        LogForget(mem.Meta.NpcCfgId, reason,
            (c.Kind == ChunkKind.Summary ? "摘要" : "对话") + " #" + chunkId + " " + Truncate(c.Text ?? "", 60));
        return true;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    /// <summary>全量重写 chunks.jsonl（追加式文件，改/删必须重写）。</summary>
    private static void RewriteChunks(NpcMemory mem)
    {
        var dir = MemoryPaths.MemDir(mem.Meta.NpcCfgId);
        System.IO.Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        foreach (var c in mem.Chunks)
            sb.AppendLine(JsonSerializer.Serialize(c, JsonOpts));
        WriteTextAtomic(System.IO.Path.Combine(dir, "chunks.jsonl"), sb.ToString().TrimEnd('\n'));
    }

    /// <summary>追一条遗忘/删除记录。</summary>
    public static void LogForget(string npcCfgId, string reason, string detail)
    {
        var path = System.IO.Path.Combine(MemoryPaths.MemDir(npcCfgId), "遗忘纪录.log");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {reason}  {detail}";
        System.IO.File.AppendAllText(path, line + "\n", new System.Text.UTF8Encoding(false));
    }

    // ================= 骨架的文本格式 =================

    /// <summary>
    /// skeleton.txt 格式：
    ///   [关系]
    ///   玩家是她的徒弟
    ///   两人已结为道侣
    ///   [处境]
    ///   正在追查一桩亏空
    /// </summary>
    private static string RenderSkeleton(Skeleton sk)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[关系]");
        foreach (var r in sk.Relations)
            if (!string.IsNullOrWhiteSpace(r)) sb.AppendLine(r);
        sb.AppendLine();
        sb.AppendLine("[处境]");
        sb.AppendLine(sk.Situation ?? "");
        return sb.ToString();
    }

    private static Skeleton ParseSkeleton(string text)
    {
        var sk = new Skeleton();
        var section = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim() == "[关系]") { section = "rel"; continue; }
            if (line.Trim() == "[处境]") { section = "sit"; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (section == "rel") sk.Relations.Add(line.Trim());
            else if (section == "sit")
            {
                if (string.IsNullOrEmpty(sk.Situation)) sk.Situation = line.Trim();
                else sk.Situation += "\n" + line.Trim();
            }
        }
        return sk;
    }

    // ================= 工具 =================

    private static string ReadText(string path) =>
        System.IO.File.ReadAllText(path, new System.Text.UTF8Encoding(false));

    private static System.Collections.Generic.IEnumerable<string> ReadLines(string path)
    {
        using var sr = new System.IO.StreamReader(path, new System.Text.UTF8Encoding(false));
        string line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }

    private static void WriteTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}
