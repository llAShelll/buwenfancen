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

        // ★ 2026-11-02 主人定：这里**不自动生成**过往总述。
        //   总述由玩家点「立即整理」触发（乙方案：调一次模型把历史压成笼统目录），
        //   不自动跑、不偷拼摘要块——拼出来太细，会变成第二份摘要。

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

    /// <summary>
    /// ★ 2026-11-03：**追加**一段摘要（恢复设计原意：摘要段不合并、不覆盖、不滚动）。
    /// · 段号 = segmentTurn（本次抽取覆盖的最后一轮对话）；同一段号重抽时只替换这一段（幂等）
    /// · segmentTurn <= 0 时退回「最后一块 Turn + 1」
    /// · 每句一条 chunk，LastSeenTurn = 段号（供轮数老化）
    /// · 超 maxChars 按句界截断（同旧逻辑）
    /// 与 ReplaceSummaryChunks 的区别：**不删其他段**。
    /// </summary>
    public static int AppendSummarySegment(NpcMemory mem, List<string> summaries, int segmentTurn, int maxChars = 500)
    {
        if (mem == null || summaries == null || summaries.Count == 0) return 0;

        var dir = MemoryPaths.MemDir(mem.Meta.NpcCfgId);
        System.IO.Directory.CreateDirectory(dir);

        var turn = segmentTurn > 0
            ? segmentTurn
            : (mem.Chunks.Count > 0 ? mem.Chunks[^1].Turn + 1 : 1);

        // 幂等：同一段号（同 Turn）已有摘要 → 先删该段，避免重抽时重复
        var dupIds = mem.Chunks
            .Where(c => c.Kind == ChunkKind.Summary && c.Turn == turn)
            .Select(c => c.Id).ToList();
        if (dupIds.Count > 0)
        {
            mem.Chunks = mem.Chunks.Where(c => !(c.Kind == ChunkKind.Summary && c.Turn == turn)).ToList();
            foreach (var id in dupIds) mem.Vectors.Remove(id);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
                LastSeenTurn = turn,
            };
            chunk.Id = mem.NextChunkId++;
            mem.Chunks.Add(chunk);
            totalChars += sentence.Length;
            added++;

            var v = EmbedderProvider.Embed(chunk.Text);
            if (v != null) mem.Vectors.Add(chunk.Id, v);
        }

        // 全量重写 chunks.jsonl + 向量落盘
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in mem.Chunks)
                sb.AppendLine(JsonSerializer.Serialize(c, JsonOpts));
            WriteTextAtomic(System.IO.Path.Combine(dir, "chunks.jsonl"), sb.ToString().TrimEnd('\n'));
        }
        catch { }
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
    /// skeleton.txt 格式（★ 2026-11-02 加 [过往总述]；★ 2026-11-07 改分代）：
    ///   [关系]
    ///   玩家是她的徒弟
    ///   [过往总述]
    ///   |0|（迁移来的旧总述，整段一行）
    ///   |1|他应下替我炼压制丹药，头一粒我先试。
    ///   |2|我们在客栈定下婚约，他要先办成喜事再回焚天宗。
    ///   [处境]
    ///   正在追查一桩亏空
    ///
    /// 兼容：没有 |N| 前缀的行 = 旧格式，加载后整段转成第 0 代。
    /// </summary>
    private static string RenderSkeleton(Skeleton sk)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[关系]");
        foreach (var r in sk.Relations)
            if (!string.IsNullOrWhiteSpace(r)) sb.AppendLine(r);
        sb.AppendLine();
        sb.AppendLine("[过往总述]");
        if (sk.PastGens != null && sk.PastGens.Count > 0)
        {
            foreach (var g in sk.PastGens)
            {
                if (g == null || string.IsNullOrWhiteSpace(g.Text)) continue;
                // 单行输出：把内部换行压成空格，避免破坏 |N| 行格式
                var one = g.Text.Replace("\r", " ").Replace("\n", " ").Trim();
                sb.Append("|").Append(g.Gen).Append("|").AppendLine(one);
            }
        }
        else
        {
            sb.AppendLine((sk.PastSummary ?? "").Trim());
        }
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
            if (line.Trim() == "[过往总述]") { section = "past"; continue; }
            if (line.Trim() == "[处境]") { section = "sit"; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (section == "rel") sk.Relations.Add(line.Trim());
            else if (section == "past")
            {
                var t = line.Trim();
                // ★ 2026-11-07 新格式：|代|内容
                var m = System.Text.RegularExpressions.Regex.Match(t, @"^\|(\d+)\|(.*)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var gen))
                {
                    sk.PastGens.Add(new PastGen { Gen = gen, Text = m.Groups[2].Value.Trim(), Turn = 0 });
                }
                else
                {
                    // 旧格式：多行整段合并进 PastSummary，下面统一转第 0 代
                    if (string.IsNullOrEmpty(sk.PastSummary)) sk.PastSummary = t;
                    else sk.PastSummary += "\n" + t;
                }
            }
            else if (section == "sit")
            {
                if (string.IsNullOrEmpty(sk.Situation)) sk.Situation = line.Trim();
                else sk.Situation += "\n" + line.Trim();
            }
        }

        // ★ 2026-11-07 迁移：旧格式 → 第 0 代（原样保留，不截断，避免一次性丢信息）
        if (sk.PastGens.Count == 0 && !string.IsNullOrWhiteSpace(sk.PastSummary))
        {
            sk.PastGens.Add(new PastGen
            {
                Gen = 0,
                Text = sk.PastSummary.Replace("\r", " ").Replace("\n", " ").Trim(),
                Turn = 0
            });
        }

        // ★ 2026-11-07：加载时也要按代数上限裁（正常只在新增时裁，但手改文件/旧数据可能超）
        while (sk.PastGens.Count > Skeleton.MaxPastGens)
            sk.PastGens.RemoveAt(0);

        return sk;
    }

    /// <summary>
    /// 把模型输出的总述规范成**一段**（对齐 1.1.2 风格：一段连贯叙述，不是分行清单）。
    /// 为什么要有这一步：同一版提示词，对话多的角色模型会分行写、对话少的写成一段，
    /// 不统一的话玩家看到的就是两种风格。★ 2026-11-02 主人反馈后加。
    /// </summary>
    public static string NormalizePastSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select(x => System.Text.RegularExpressions.Regex.Replace(x, @"^[\-\*·•]?\s*(\d+[\.\、\)])?\s*", ""))
            .Where(x => x.Length > 0)
            .ToList();
        if (lines.Count == 0) return "";
        if (lines.Count == 1) return lines[0];
        var sb = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
            sb.Append(l);
            var last = l[l.Length - 1];
            if ("。！？；…".IndexOf(last) < 0) sb.Append('。');
        }
        return sb.ToString();
    }

    /// <summary>按「句」裁到上限：超了从最早的句子开始丢（越早越淡），给一段式总述用。</summary>
    public static string ClampPastSummaryText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        if (text.Length <= maxChars) return text;
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[。！？；])")
            .Where(p => p.Length > 0).ToList();
        while (parts.Count > 1 && string.Concat(parts).Length > maxChars)
            parts.RemoveAt(0);
        var s = string.Concat(parts);
        if (s.Length > maxChars) s = s.Substring(s.Length - maxChars);
        return s.Trim();
    }

    /// <summary>
    /// 把若干行拼成「过往总述」：超上限时**从最早的一行开始丢**，直到不超。
    /// ★ 2026-11-02 主人定：模型写超了先让它自己简写最早的（提示词里要求），
    ///   程序这边兜底只做「丢最早」，不偷偷改中段。
    /// </summary>
    public static string ClampPastSummaryLines(IList<string> lines, int maxChars)
    {
        if (lines == null || lines.Count == 0) return "";
        var kept = new List<string>();
        foreach (var l in lines)
            if (!string.IsNullOrWhiteSpace(l)) kept.Add(l.Trim());
        if (kept.Count == 0) return "";
        string Join() => string.Join("\n", kept);
        while (kept.Count > 1 && Join().Length > maxChars)
            kept.RemoveAt(0);
        var text = Join();
        if (text.Length > maxChars) text = text.Substring(text.Length - maxChars);   // 最后一行也超 → 留末尾
        return text.Trim();
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
