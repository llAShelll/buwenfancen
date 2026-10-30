using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>检索命中项（chunk 或 fact）</summary>
public sealed class RetrievedItem
{
    public bool IsFact { get; set; }
    public Chunk Chunk { get; set; }
    public Fact Fact { get; set; }
    public double Score { get; set; }
}

/// <summary>
/// 记忆检索（阶段 3）：
/// ① BM25 字面检索
/// ② BGE 语义检索 + RRF 融合
/// 两路都只对 chunk 做；facts 单独走 BM25（数量少，字面够用）。
/// </summary>
public static class MemoryRetriever
{
    private const int RrfK = 60;
    private const int CandidatePool = 20;      // 每路取多少候选再融合
    private const float BgeMinSim = 0.50f;     // BGE 最低相似度（0.40 会把 0.47 的噪声放进来）
    private const double BgeWeight = 2.0;      // RRF 里 BGE 权重（语义主导）
    private const double Bm25MinScore = 1.0;   // BM25 最低分数（实测：有效命中 2~5，噪声 0.2~0.5）

    /// <summary>
    /// 排除最近 N 条 chunk：游戏的 history 已经带了最近若干轮对话，
    /// 我们再检索出来就是重复注入（浪费 token + 干扰注意力）。
    ///
    /// ★ 2026-10-25 主人定：8 → 3 → 2。
    ///   实测游戏 history 只带 295 字 ≈ 2 条消息（玩家问一句 + 角色答一句 = 1 轮），
    ///   而 chunk 总共才 2~15 条 → 排 8 条（约 4 轮）等于把「唯一有内容的」也扔了，检索必然空。
    ///   排 2 条正好等于 history 实际带的量，不多排也不少排。
    ///   （这个值主人自己会调，不进 config.json）
    /// </summary>
    public static int ExcludeRecentChunks = 2;

    /// <summary>
    /// 诊断日志开关。
    /// 用户 01:55 定：只保留关键日志（抽取/配置/REPLACE），关掉每轮都刷的检索日志。
    /// </summary>
    public static bool _diagnose = false;

    private sealed class Cache
    {
        public Bm25 ChunkIndex;
        public Bm25 FactIndex;
        public int ChunkCount;
        public int FactCount;
        public Dictionary<int, int> IdToIdx;   // chunkId → chunks 列表索引
    }

    private static readonly Dictionary<string, Cache> _cache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// 检索，返回按相关度降序的命中项。
    ///
    /// ★ 2026-10-24（3.5-B ①②）：
    ///   kinds  —— 只返回指定类型的 chunk（如 "worldbook"），世界书用
    ///   npcs   —— 只返回「归属或同场」在这些 NPC 里的 chunk，多 NPC 同场用
    ///   两者都传 null 就是原行为（不过滤）。
    /// </summary>
    public static List<RetrievedItem> Search(NpcMemory mem, string query, int topK = 6,
        IReadOnlyCollection<string> kinds = null,
        IReadOnlyCollection<string> npcs = null)
    {
        var result = new List<RetrievedItem>();
        if (mem == null || string.IsNullOrWhiteSpace(query)) return result;
        if (mem.Chunks.Count == 0 && mem.Facts.Count == 0) return result;

        var cache = GetCache(mem);

        // ① chunk：BM25 排名（低于最低分数的丢弃：避免靠「吗」这种单字蒙中）
        var bm25Hits = new List<(int Idx, double Score)>();
        foreach (var (docIdx, score) in cache.ChunkIndex.Search(query, CandidatePool))
        {
            if (score < Bm25MinScore) continue;
            bm25Hits.Add((docIdx, score));
        }

        // ② chunk：BGE 语义排名（模型不可用则为空）
        var bgeHits = new List<(int Idx, double Score)>();
        var qv = EmbedderProvider.Embed(query);
        if (qv != null)
        {
            foreach (var (chunkId, sim) in mem.Vectors.Search(qv, CandidatePool, BgeMinSim))
            {
                if (cache.IdToIdx.TryGetValue(chunkId, out var idx))
                    bgeHits.Add((idx, sim));
            }
        }

        // ③ RRF 融合
        // ★ 排除最近 N 条：游戏 history 已带最近几轮，避免重复注入
        HashSet<int> recentIds = null;
        if (ExcludeRecentChunks > 0 && mem.Chunks.Count > ExcludeRecentChunks)
        {
            recentIds = mem.Chunks
                .OrderByDescending(c => c.CreatedAt)
                .Take(ExcludeRecentChunks)
                .Select(c => c.Id)
                .ToHashSet();
        }

        var fused = RrfFuse(bm25Hits, bgeHits);

        // ★ 2026-10-25：先收集全部候选，再做「时间加权」和排序，最后截 topK。
        //   原来是一路 Add 一路 break，时间加权来不及参与「谁能进 topK」的竞争。
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pool = new List<RetrievedItem>();
        foreach (var (idx, score) in fused)
        {
            if (idx < 0 || idx >= mem.Chunks.Count) continue;
            if (recentIds != null && recentIds.Contains(mem.Chunks[idx].Id)) continue;   // 跳过最近的
            var c = mem.Chunks[idx];
            // ★ 3.5-B ① kinds 过滤：只保留指定类型的 chunk（世界书用）
            if (kinds != null && kinds.Count > 0 && !kinds.Contains(c.Kind)) continue;
            // ★ 3.5-B ② npcs 过滤：只保留「归属或同场」在这些 NPC 里的 chunk
            if (npcs != null && npcs.Count > 0 && !NpcMatches(c, npcs)) continue;

            // ★ 时间加权：越近的事越重要。
            //   检索本身只按「相关度」排，会漏掉「玩家问的是最近发生的事」这种场景。
            //   衰减曲线：24 小时内 ×1.25、72 小时内 ×1.12，更早不加权。
            var ageHours = (nowTs - c.CreatedAt) / 3600.0;
            if (ageHours < 0) ageHours = 0;
            var timeWeight = ageHours < 24 ? 1.25 : (ageHours < 72 ? 1.12 : 1.0);

            pool.Add(new RetrievedItem { IsFact = false, Chunk = c, Score = score * timeWeight });
        }

        // 按加权后的分数降序，取前 topK
        result = pool.OrderByDescending(x => x.Score).Take(topK).ToList();

        // ★ 2026-10-26：结果去重——摘要句之间、摘要与原文之间高度重合时只留信息量大的一条。
        //   玩家反馈「重复同一件事」：多条记忆讲同一段经历时，模型会跟着复述几遍。
        result = DedupChunks(result);

        // ★ 诊断日志（测试期开，正式版可关）
        if (_diagnose && !string.IsNullOrWhiteSpace(query))
        {
            var bm = bm25Hits.Take(4).Select(h => $"{mem.Chunks[h.Idx].Id}:{h.Score:F3}");
            var bg = bgeHits.Take(4).Select(h => $"{mem.Chunks[h.Idx].Id}:{h.Score:F2}");
            var rec = recentIds != null ? string.Join(",", recentIds.OrderBy(x => x)) : "无";
            var fin = string.Join(",", result.Select(r => r.Chunk?.Id ?? r.Fact?.Text?.Length));
            MemoryEngine.Log?.Invoke(
                $"[检索] q=\"{query[..Math.Min(20, query.Length)]}\" chunks={mem.Chunks.Count} vecs={mem.Vectors.Count} " +
                $"BM25=[{string.Join(" ", bm)}] BGE=[{string.Join(" ", bg)}] 排除=[{rec}] 最终=[{fin}]", false);
        }

        // ④ facts 单独 BM25（不参与 RRF，避免长文本压过短事实）
        var factLeft = Math.Max(0, topK - result.Count);
        if (factLeft > 0 && mem.Facts.Count > 0)
        {
            foreach (var (docIdx, score) in cache.FactIndex.Search(query, factLeft))
            {
                if (docIdx < 0 || docIdx >= mem.Facts.Count) continue;
                result.Add(new RetrievedItem { IsFact = true, Fact = mem.Facts[docIdx], Score = score });
            }
        }

        return result;
    }

    /// <summary>chunk 是否归属或同场于 npcs 列表里的任一角色</summary>
    private static bool NpcMatches(Chunk c, IReadOnlyCollection<string> npcs)
    {
        if (c == null) return false;
        if (!string.IsNullOrEmpty(c.Npc) && npcs.Contains(c.Npc)) return true;
        if (c.Witness != null)
            foreach (var w in c.Witness)
                if (!string.IsNullOrEmpty(w) && npcs.Contains(w)) return true;
        return false;
    }

    /// <summary>RRF 融合：score = Σ weight/(k + rank)</summary>
    /// <summary>结果去重：文本高度重合的只保留一条（保留分数更高的，即先出现的）。</summary>
    private static List<RetrievedItem> DedupChunks(List<RetrievedItem> items)
    {
        if (items == null || items.Count <= 1) return items ?? new List<RetrievedItem>();
        var kept = new List<RetrievedItem>();
        foreach (var it in items)
        {
            var text = NormalizeForDedup(it.Chunk?.Text ?? "");
            if (text.Length < 8) { kept.Add(it); continue; }
            var dup = false;
            foreach (var k in kept)
            {
                var other = NormalizeForDedup(k.Chunk?.Text ?? "");
                if (other.Length < 8) continue;
                if (IsTooSimilar(text, other)) { dup = true; break; }
            }
            if (!dup) kept.Add(it);
        }
        return kept;
    }

    /// <summary>去掉说话人前缀和空白，避免「玩家：」「角色：」把相似度拉高。</summary>
    private static string NormalizeForDedup(string s)
    {
        return s.Replace("玩家：", "").Replace("角色：", "")
                .Replace(" ", "").Replace("\r", "").Replace("\n", "");
    }

    /// <summary>包含关系，或字符 bigram 重合率 &gt; 0.60（且共同 bigram ≥ 6），视为同一件事的重复表述。</summary>
    private static bool IsTooSimilar(string a, string b)
    {
        if (a.Length >= 10 && b.Contains(a)) return true;
        if (b.Length >= 10 && a.Contains(b)) return true;
        var ba = Bigrams(a);
        var bb = Bigrams(b);
        if (ba.Count == 0 || bb.Count == 0) return false;
        var inter = 0;
        foreach (var x in ba) if (bb.Contains(x)) inter++;
        var union = ba.Count + bb.Count - inter;
        // ★ 2026-10-28：0.72 → 0.60，并加「共同 bigram ≥ 6」防短句误判。
        //   依据：记忆测试实测，0.72 太严导致重复表述占位、真正不同的记忆被挤掉。
        return union > 0 && inter >= 6 && (double)inter / union > 0.60;
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
        return set;
    }

    private static List<(int Idx, double Score)> RrfFuse(
        List<(int Idx, double Score)> listA,
        List<(int Idx, double Score)> listB)
    {
        var scores = new Dictionary<int, double>();

        for (var i = 0; i < listA.Count; i++)
        {
            var idx = listA[i].Idx;
            scores[idx] = (scores.TryGetValue(idx, out var s) ? s : 0) + 1.0 / (RrfK + i + 1);
        }
        for (var i = 0; i < listB.Count; i++)
        {
            var idx = listB[i].Idx;
            scores[idx] = (scores.TryGetValue(idx, out var s) ? s : 0) + BgeWeight / (RrfK + i + 1);
        }

        var result = new List<(int, double)>();
        foreach (var kv in scores) result.Add((kv.Key, kv.Value));
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    private static Cache GetCache(NpcMemory mem)
    {
        var key = mem.Meta.NpcCfgId ?? "";
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var c)
                && c.ChunkCount == mem.Chunks.Count
                && c.FactCount == mem.Facts.Count)
            {
                return c;
            }

            var chunkDocs = new List<string>(mem.Chunks.Count);
            var idToIdx = new Dictionary<int, int>(mem.Chunks.Count);
            for (var i = 0; i < mem.Chunks.Count; i++)
            {
                chunkDocs.Add(mem.Chunks[i].Text ?? "");
                idToIdx[mem.Chunks[i].Id] = i;
            }

            var factDocs = new List<string>(mem.Facts.Count);
            for (var i = 0; i < mem.Facts.Count; i++)
                factDocs.Add((mem.Facts[i].Text ?? "") + " " + (mem.Facts[i].Span ?? ""));

            c = new Cache
            {
                ChunkIndex = Bm25.Build(chunkDocs),
                FactIndex = Bm25.Build(factDocs),
                ChunkCount = mem.Chunks.Count,
                FactCount = mem.Facts.Count,
                IdToIdx = idToIdx,
            };
            _cache[key] = c;
            return c;
        }
    }

    /// <summary>作废某角色的检索缓存（写入新 chunk / 删除后调用）。</summary>
    public static void Invalidate(string npcCfgId)
    {
        lock (_lock) _cache.Remove(npcCfgId ?? "");
    }

    /// <summary>
    /// 组装命中层的注入文本（相关回忆）。
    /// ★ 挑选按相关度【降序】（预算内优先保留最相关的），
    ///   输出按相关度【升序】（最相关放最后，利用 LLM 对末尾注意力最高）。
    /// </summary>
    public static string BuildRecallBlock(List<RetrievedItem> items, int maxChars = 1000)
    {
        if (items == null || items.Count == 0) return "";

        // ① 按相关度降序挑选：预算内优先保住最相关的
        var selected = new List<(RetrievedItem Item, string Line)>();
        var used = 0;
        foreach (var item in items.OrderByDescending(i => i.Score))
        {
            var line = BuildLine(item);
            if (used + line.Length > maxChars) continue;   // 这条塞不下，试下一条（不 break，避免浪费预算）
            selected.Add((item, line));
            used += line.Length;
        }
        if (selected.Count == 0) return "";

        // ② 输出按相关度升序：最相关的放最后
        var sb = new StringBuilder();
        sb.AppendLine("## 相关回忆");
        foreach (var (_, line) in selected.OrderBy(x => x.Item.Score))
            sb.AppendLine(line);
        return sb.ToString();
    }

    private static string BuildLine(RetrievedItem item)
    {
        string line;
        if (item.IsFact)
        {
            var f = item.Fact;
            line = $"- [事实·{f.Importance}级] {f.Text}";
        }
        else
        {
            var c = item.Chunk;
            var kindLabel = c.Kind == ChunkKind.Summary ? "摘要" : "对话";
            line = $"- [{kindLabel}] {c.Text}";
        }
        if (line.Length > 250) line = line.Substring(0, 250) + "…";
        return line;
    }
}
