using System.Text;
using System.Text.Json;

namespace BuWenFanChen.Memory;

/// <summary>
/// 抽取流程：每积累 N 条 chunk，调 API 生成摘要段 + 抽取 facts + 更新骨架。
/// 在后台线程运行（不碰 Unity 对象），完成后写盘并作废缓存。
/// </summary>
public static class Extractor
{
    /// <summary>每积累多少条 dialog 抽一次（正式值 48；测试期曾临时改为 8）</summary>
    public static int ExtractInterval => PromptConfig.ExtractInterval;   // 阶段 4-A：改读 config.json

    /// <summary>5 级事实必须命中的里程碑词</summary>
    private static readonly string[] MilestoneWords =
    {
        "结契", "拜师", "名分", "道侣", "托付", "认主", "成婚", "结缘", "收徒", "义结"
    };

    private static readonly object _lock = new();
    private static bool _running;

    /// <summary>
    /// 是否该抽了。
    /// ★ 只按 dialog 条数判断：summary 会被 ReplaceSummaryChunks 替换，
    ///   若按总数判断，摘要一减就会让下次永远不触发 ✗
    /// </summary>
    public static bool ShouldExtract(NpcMemory mem)
    {
        if (mem == null) return false;
        var dialogCount = mem.Chunks.Count(c => c.Kind == ChunkKind.Dialog);
        return dialogCount >= mem.Meta.LastExtractTurn + ExtractInterval;
    }

    /// <summary>触发一次抽取（后台跑，不阻塞调用方）。</summary>
    public static void Trigger(NpcMemory mem, LlmClient.Config cfg, Action<string, bool> log)
    {
        if (mem == null || cfg == null || !cfg.IsValid) return;
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }

        // 抓一份快照（避免后台线程碰缓存对象）
        var snapshot = TakeSnapshot(mem);
        var cfgCopy = new LlmClient.Config { Url = cfg.Url, ApiKey = cfg.ApiKey, Model = cfg.Model };

        Task.Run(async () =>
        {
            try
            {
                var result = await RunAsync(snapshot, cfgCopy, log).ConfigureAwait(false);
                if (result) log?.Invoke("[抽取] 完成", false);
            }
            catch (Exception e)
            {
                log?.Invoke($"[抽取] 失败: {e.GetType().Name}: {e.Message}", true);
            }
            finally
            {
                lock (_lock) _running = false;
            }
        });
    }

    /// <summary>
    /// 同步抽取（2026-10-27 主人要：「立即整理」按钮用）。
    /// 把当前所有未整理的对话立刻提炼成【摘要 + facts + 骨架】，等它跑完再返回。
    /// </summary>
    public static (bool Ok, string Msg) ExtractNow(NpcMemory mem, LlmClient.Config cfg)
    {
        if (mem == null || cfg == null || !cfg.IsValid) return (false, "配置或记忆不可用");
        lock (_lock)
        {
            if (_running) return (false, "已有抽取在进行中，稍等");
            _running = true;
        }
        try
        {
            var snapshot = TakeSnapshot(mem);
            var cfgCopy = new LlmClient.Config { Url = cfg.Url, ApiKey = cfg.ApiKey, Model = cfg.Model };
            var result = RunAsync(snapshot, cfgCopy, (m, w) => { }).GetAwaiter().GetResult();
            return result ? (true, "整理完成（摘要/事实/骨架已更新）") : (false, "没有新的对话可整理（都已经整理过了）");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    private sealed class Snapshot
    {
        public string NpcCfgId;
        public Skeleton Skeleton;
        public List<Chunk> Chunks;
        public List<Fact> Facts;
        public int FromChunkCount;   // 上次抽到哪
    }

    private static Snapshot TakeSnapshot(NpcMemory mem)
    {
        return new Snapshot
        {
            NpcCfgId = mem.Meta.NpcCfgId,
            Skeleton = new Skeleton
            {
                Relations = new List<string>(mem.Skeleton.Relations),
                Situation = mem.Skeleton.Situation,
            },
            Chunks = new List<Chunk>(mem.Chunks),
            Facts = new List<Fact>(mem.Facts),
            FromChunkCount = mem.Meta.LastExtractTurn,
        };
    }

    // ================= 主流程 =================

    private static async Task<bool> RunAsync(Snapshot snap, LlmClient.Config cfg, Action<string, bool> log)
    {
        // 取待整理的对话：按【dialog 序号】切（不是列表位置）
        // LastExtractTurn 记的是「已抽到第几条 dialog」，与 summary 无关
        var dialogs = snap.Chunks.Where(c => c.Kind == ChunkKind.Dialog).ToList();
        var todo = dialogs.Skip(snap.FromChunkCount).ToList();
        if (todo.Count == 0) return false;

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(snap, todo);

        log?.Invoke($"[抽取] 开始：{todo.Count} 轮新对话（总 dialog {dialogs.Count}，上次已抽 {snap.FromChunkCount}）→ API", false);
        var (content, error) = await LlmClient.CompleteAsync(cfg, systemPrompt, userPrompt, 3000)
            .ConfigureAwait(false);
        if (content == null)
        {
            log?.Invoke($"[抽取] API 失败: {error}", true);
            return false;
        }

        // 解析 JSON
        var parsed = ParseExtraction(content);
        if (parsed == null)
        {
            log?.Invoke($"[抽取] JSON 解析失败: {content[..Math.Min(200, content.Length)]}", true);
            return false;
        }

        // 从盘上新加载一份（避免覆盖期间玩家的新操作）
        var mem = MemoryStore.Load(snap.NpcCfgId);

        var (addedRelations, updatedFacts, addedSummary) = Apply(mem, parsed, todo, snap.NpcCfgId, log);

        // 落盘
        // ★ 记 dialog 条数（不是总数）：摘要替换会让总数减少，按总数会导致永远不再触发
        mem.Meta.LastExtractTurn = mem.Chunks.Count(c => c.Kind == ChunkKind.Dialog);
        MemoryStore.Save(mem);
        MemoryRetriever.Invalidate(snap.NpcCfgId);

        // ★ 老化：turn 恒为 0 导致 turn%48 永远不触发，改在这里按抽取节奏跑
        try
        {
            Aging.AgeFacts(mem, snap.NpcCfgId);
            Aging.EnforceLevel4Cap(mem, snap.NpcCfgId);
            Aging.EvictDialogChunks(mem, snap.NpcCfgId);
            MemoryStore.Save(mem);
        }
        catch { }

        // ★ 内存缓存里的 LastExtractTurn 还是旧值 → 必须作废，否则会重复触发抽取
        MemoryEngine.InvalidateCache(snap.NpcCfgId);

        log?.Invoke(
            $"[抽取] 完成：关系+{addedRelations} 事实+{updatedFacts} 摘要句+{addedSummary}", false);
        return true;
    }

    // ================= Prompt =================

    private static string BuildSystemPrompt() =>
        """
你是记忆整理器。根据对话产出结构化记忆。只输出一个 JSON 对象，不要任何解释。

JSON 格式：
{
  "relations": ["新增的关系条目"],
  "situation": "当前处境一句话",
  "summary": ["摘要句1", "摘要句2"],
  "facts": [
    {"category": "Gift|Promise|RelationMilestone|ImportantEvent",
     "text": "一句话事实",
     "importance": 1,
     "span": "原文逐字片段"}
  ]
}

规则：
- relations：只写【新增】的关系（师徒/道侣/亲友/仇敌/主仆等），已有的不要重复。没有就给 []。
- ★ relations 与 facts 互斥：关系本身（谁是谁的师徒/道侣/亲友）只写进 relations，
  不要再把「玩家与角色为道侣/夫妻」「玩家为角色徒弟」这类关系事实写进 facts。
  facts 只写「关系之外」的里程碑（承诺、生死相托、名分之外的转折、影响关系走向的具体事件）。
- situation：当前处境一句话，会替换旧值。没有明显变化就给 ""。
- summary：3~8 句，按时间顺序，每句一个完整事件，总字数不超过 500。没有就给 []。
- facts：只写「如果忘了玩家会觉得不可接受」的事。
     · importance 5 = 刻骨铭心，只给这四种：结契、拜师、名分、生死相托
         （道侣/认主/成婚/结缘 只有明确确立名分时才算 5，日常互称不算）
     · importance 4 = 重要转折，只给这四种：
         a. 影响关系走向的重大承诺
         b. 关键剧情转折
         c. 付出重大代价的事
         d. 明确的关系升级
       日常小约定（递水、收东西、改天再说这类）最多 3 级。
       「被再次提及」最多升到 4 级，且必须满足上面四种之一。
     · importance 3 = 有明确影响，值得记住
     · 1~2 = 琐事，不要给
     · span 必须是从对话原文里逐字复制的片段（用于校验），至少 10 字
     · text 一句话，不超过 60 字
     · 没有就给 []
""";

    private static string BuildUserPrompt(Snapshot snap, List<Chunk> todo)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## 已有骨架");
        sb.AppendLine("关系：" + (snap.Skeleton.Relations.Count > 0
            ? string.Join("；", snap.Skeleton.Relations) : "（无）"));
        sb.AppendLine("处境：" + (string.IsNullOrEmpty(snap.Skeleton.Situation)
            ? "（无）" : snap.Skeleton.Situation));
        sb.AppendLine();

        var keyFacts = snap.Facts.Where(f => f.Importance >= 4).ToList();
        if (keyFacts.Count > 0)
        {
            sb.AppendLine("## 已有重要事实（避免重复）");
            foreach (var f in keyFacts)
                sb.AppendLine($"- [{f.Importance}级] {f.Text}");
            sb.AppendLine();
        }

        sb.AppendLine("## 待整理对话");
        foreach (var c in todo)
            sb.AppendLine(c.Text);
        sb.AppendLine();

        sb.AppendLine("请输出 JSON。");
        return sb.ToString();
    }

    // ================= 解析 + 校验 =================

    private sealed class ParsedExtraction
    {
        public List<string> Relations = new();
        public string Situation = "";
        public List<string> Summary = new();
        public List<ParsedFact> Facts = new();
    }

    private sealed class ParsedFact
    {
        public string Category;
        public string Text;
        public int Importance;
        public string Span;
    }

    private static ParsedExtraction ParseExtraction(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var p = new ParsedExtraction();

            if (root.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
                foreach (var r in rels.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String)
                    {
                        var s = r.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) p.Relations.Add(s.Trim());
                    }

            if (root.TryGetProperty("situation", out var sit) && sit.ValueKind == JsonValueKind.String)
                p.Situation = sit.GetString()?.Trim() ?? "";

            if (root.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Array)
                foreach (var s in sum.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String)
                    {
                        var t = s.GetString();
                        if (!string.IsNullOrWhiteSpace(t)) p.Summary.Add(t.Trim());
                    }

            if (root.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in facts.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    var pf = new ParsedFact
                    {
                        Category = f.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String
                            ? cat.GetString() : "ImportantEvent",
                        Text = f.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
                            ? tx.GetString()?.Trim() : "",
                        Span = f.TryGetProperty("span", out var sp) && sp.ValueKind == JsonValueKind.String
                            ? sp.GetString()?.Trim() : "",
                    };
                    if (f.TryGetProperty("importance", out var imp) && imp.ValueKind == JsonValueKind.Number)
                        pf.Importance = imp.GetInt32();
                    if (!string.IsNullOrWhiteSpace(pf.Text)) p.Facts.Add(pf);
                }
            }
            return p;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return raw.Substring(start, i - start + 1);
        }
        return null;
    }

    // ================= 应用 + 校验 =================

    private static (int Relations, int Facts, int Summary) Apply(
        NpcMemory mem, ParsedExtraction p, List<Chunk> todo, string npcCfgId, Action<string, bool> log)
    {
        var rawText = string.Join("\n", todo.Select(t => t.Text));

        // ① 骨架·关系（append-only，去重）
        var addedRel = 0;
        foreach (var r in p.Relations)
        {
            if (mem.Skeleton.Relations.Any(x => x == r)) continue;
            mem.Skeleton.Relations.Add(r);
            addedRel++;
        }

        // ② 骨架·处境（替换）
        if (!string.IsNullOrWhiteSpace(p.Situation))
            mem.Skeleton.Situation = p.Situation;

        // ③ 摘要段：替换旧的（摘要是对历史的总述，不是增量追加）
        var addedSummary = MemoryStore.ReplaceSummaryChunks(mem, p.Summary, 500);

        // ④ facts（含程序校验）
        var addedFacts = 0;
        foreach (var pf in p.Facts)
        {
            var text = pf.Text.Length > 120 ? pf.Text[..120] : pf.Text;
            var span = pf.Span ?? "";
            var imp = Math.Clamp(pf.Importance, 1, 5);

            // 校验 1：span 必须是原文子串。
            // ★ 2026-10-26：放宽——先归一化（去空白、统一标点、去引号省略号）再匹配；
            //   归一化仍不中时，只要 span 的 2 字片段在原文里覆盖率 ≥ 0.6 也认。
            //   原来逐字节 Contains 太严：模型常改标点/加省略号/换引号，正常事实被误判成
            //   「span 不是原文」直接丢弃 → 玩家反馈「记忆越来越少 / 失忆」。
            if (!string.IsNullOrEmpty(span))
            {
                var normSpan = NormalizeSpan(span);
                var normRaw = NormalizeSpan(rawText);
                var inRaw = normSpan.Length > 0 && normRaw.Contains(normSpan);
                if (!inRaw && normSpan.Length >= 6)
                {
                    inRaw = BigramOverlap(normSpan, normRaw) >= 0.6;
                    if (inRaw)
                        log?.Invoke($"[抽取] span 与原文有出入但内容吻合，按原文收下：「{text}」", false);
                }
                if (!inRaw)
                {
                    log?.Invoke($"[抽取] 丢弃 fact（span 不是原文）：「{text}」", true);
                    continue;
                }
            }

            // 校验 2：5 级必须命中里程碑词
            if (imp == 5 && !MilestoneWords.Any(w => text.Contains(w)))
            {
                imp = 4;
                log?.Invoke($"[抽取] 5级降4（未命中里程碑词）：「{text}」", true);
            }

            // 校验 3：4 级必须有 span 且 ≥ 10 字
            if (imp == 4 && span.Length < 10)
            {
                imp = 3;
                log?.Invoke($"[抽取] 4级降3（span 不足10字）：「{text}」", true);
            }

            // 去重（同文本已存在）
            if (mem.Facts.Any(f => f.Text == text)) continue;

            mem.Facts.Add(new Fact
            {
                Category = ParseCategory(pf.Category),
                Text = text,
                Importance = imp,
                Span = span,
                Npc = MemoryEngine.ResolveName(mem.Meta.NpcCfgId),
                Source = FactSource.Api,
            });
            addedFacts++;
        }

        return (addedRel, addedFacts, addedSummary);
    }

    /// <summary>校验用归一化：去空白、全角标点转半角、去掉引号和省略号。</summary>
    private static string NormalizeSpan(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            switch (ch)
            {
                case '，': sb.Append(','); break;
                case '。': sb.Append('.'); break;
                case '；': sb.Append(';'); break;
                case '：': sb.Append(':'); break;
                case '！': sb.Append('!'); break;
                case '？': sb.Append('?'); break;
                case '（': sb.Append('('); break;
                case '）': sb.Append(')'); break;
                case '“': case '”': case '"': break;
                case '‘': case '’': case '\'': break;
                case '…': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>a 的 2 字片段在 b 里出现的比例（0~1）。</summary>
    private static double BigramOverlap(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return 0;
        var setB = new HashSet<string>();
        for (var i = 0; i + 1 < b.Length; i++) setB.Add(b.Substring(i, 2));
        var hit = 0;
        var total = 0;
        for (var i = 0; i + 1 < a.Length; i++)
        {
            total++;
            if (setB.Contains(a.Substring(i, 2))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    private static FactCategory ParseCategory(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return FactCategory.ImportantEvent;
        var t = s.Trim().ToLowerInvariant();
        if (t.Contains("gift") || t.Contains("赠")) return FactCategory.Gift;        if (t.Contains("promise") || t.Contains("承")) return FactCategory.Promise;
        if (t.Contains("milestone") || t.Contains("里程") || t.Contains("关系"))
            return FactCategory.RelationMilestone;
        return FactCategory.ImportantEvent;
    }
}
