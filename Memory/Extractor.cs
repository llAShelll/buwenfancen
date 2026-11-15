using System.Text;
using System.Text.Json;

namespace BuWenFanChen.Memory;

/// <summary>
/// 抽取流程：每积累 N 条 chunk，调 API 生成摘要段 + 抽取 facts + 更新骨架。
/// 在后台线程运行（不碰 Unity 对象），完成后写盘并作废缓存。
/// </summary>
public static class Extractor
{
    /// <summary>
    /// 每积累多少条 dialog 抽一次。
    /// ★ 2026-11-07 主人定：**跟上下文档位走**（CanonMergeEvery：MAX 8 / High 24 / Low 36 / Off 48）。
    ///   原来是自己一个固定值（30），和档位里的「合并节拍」概念重复、玩家不知道该怎么调；
    ///   现在统一成「档位里的合并数字」——MAX 记忆最勤、Off 最疏，一个旋钮管完。
    ///   config.json 里的 extractInterval 字段保留但已停用（只作历史兼容）。
    /// </summary>
    public static int ExtractInterval => PromptConfig.CanonMergeEvery;

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

    private static readonly object _mergeGate = new();
    private static bool _mergeRunning;

    /// <summary>
    /// ★ 2026-11-07（主人定）：归档即合并。
    /// canon 裁掉最老 N 轮时调用，把这一段压成「过往总述」的新一代（≤30 字）。
    /// 异步执行、不阻塞本轮对话；失败只记日志——原文还在 archive_pending.jsonl + chunks.jsonl，不丢。
    /// 同一时间只跑一个（正在跑时直接跳过这一轮归档的合并，数据不会丢，下次归档会带上相邻内容）。
    /// </summary>
    public static void TriggerArchiveMerge(string npcCfgId, int pendingRounds, int remainRounds)
    {
        if (string.IsNullOrWhiteSpace(npcCfgId) || pendingRounds <= 0) return;
        var cfg = MemoryEngine.GetLlmConfig(null);
        if (cfg == null || !cfg.IsValid) return;
        var cfgCopy = new LlmClient.Config { Url = cfg.Url, ApiKey = cfg.ApiKey, Model = cfg.Model };

        var path = System.IO.Path.Combine(MemoryPaths.MemDir(npcCfgId), "archive_pending.jsonl");
        string archiveText;
        try { archiveText = ReadPendingText(path); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(archiveText)) return;
        if (archiveText.Length > 24000) archiveText = archiveText.Substring(archiveText.Length - 24000);
        var expectGen = Math.Max(1, Math.Min(4, pendingRounds / 6 + 1));

        lock (_mergeGate)
        {
            if (_mergeRunning)
            {
                MemoryEngine.Log?.Invoke("[合并] 上一次归档合并还没跑完，本轮跳过", false);
                return;
            }
            _mergeRunning = true;
        }

        Task.Run(async () =>
        {
            try
            {
                // ★★ 2026-11-11 修（有设计依据，不是猜）：
                //   `设计_骨架总述分层.md` §2.3 明确规定，改后的输入是
                //   ——「只要新对话，**不给旧总述**」。
                //   主路径 Extract 已经按这条改了（PastSummaryInput 只取最近 30 轮原文），
                //   但 11-07 后加的这条「归档合并」沿用了旧的「合并续写」思路，
                //   把 RenderPastSummary() 前 600 字当输入喂给模型 →
                //   模型看到旧事就照抄一遍进新代，实测 skeleton.txt：
                //     |3|=|7|=|11|、|4|=|8|=|12|、|6|=|10|  ← 10 代里只有 3~4 条不同内容
                //   现在按设计去掉旧总述，只给「刚被裁出的这段对话」。
                var input = "【刚被裁出原文窗口的 " + pendingRounds + " 轮对话】\n" + archiveText
                          + "\n\n请按时间顺序写这段发生了哪些事，输出 " + expectGen + " 行，每行一条事件脉络。";

                var (content, err) = await LlmClient.CompleteAsync(cfgCopy, ArchiveMergeSystem, input, 600)
                    .ConfigureAwait(false);
                if (content == null)
                {
                    MemoryEngine.Log?.Invoke("[合并] 失败（pending 保留，下次再试）：" + err, true);
                    return;
                }

                var lines = new List<string>();
                foreach (var raw in content.Split('\n'))
                {
                    var t = raw.Trim().TrimStart('-', '·', '•', ' ', '\t').Trim();
                    if (t.Length == 0) continue;
                    if (t.Length > Skeleton.GenCharLimit) t = t.Substring(0, Skeleton.GenCharLimit);
                    lines.Add(t);
                    if (lines.Count >= 4) break;
                }
                if (lines.Count == 0) return;

                var mem2 = MemoryStore.Load(npcCfgId);
                var maxTurn = mem2.Chunks.Count > 0 ? mem2.Chunks.Max(c => c.Turn) : 0;
                var gens = new List<int>();
                foreach (var t in lines)
                {
                    var gen = mem2.Skeleton.AddPastGen(t, maxTurn, out _);
                    if (gen != null) gens.Add(gen.Gen);
                }
                if (gens.Count == 0) return;

                MemoryStore.Save(mem2);
                MemoryEngine.InvalidateCache(npcCfgId);

                // ★ 只有合并成功才清空 pending；失败就留着，下次连着新的一起压（数据不丢）
                try { System.IO.File.WriteAllText(path, "", Encoding.UTF8); } catch { }

                MemoryEngine.Log?.Invoke(
                    $"[合并] 完成：{pendingRounds} 轮 → 第 {gens[0]}~{gens[gens.Count - 1]} 代总述"
                    + $"（共 {gens.Count} 代）| 当前窗口 {remainRounds} 轮", false);
            }
            catch (Exception e)
            {
                MemoryEngine.Log?.Invoke("[合并] 异常：" + e.GetType().Name + ": " + e.Message, true);
            }
            finally
            {
                lock (_mergeGate) _mergeRunning = false;
            }
        });
    }

    /// <summary>读 archive_pending.jsonl 并解成可读文本（每行 role\tbase64 一条）。</summary>
    private static string ReadPendingText(string path)
    {
        if (!System.IO.File.Exists(path)) return "";
        var sb = new StringBuilder();
        foreach (var line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var role = line.Substring(0, tab);
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(tab + 1))); }
            catch { continue; }
            sb.Append(role == "user" ? "玩家：" : "她：").AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>归档合并用的提示词：把积压的一段压成多代 ≤30 字的事件脉络。</summary>
    private const string ArchiveMergeSystem = """
你是记忆压缩员。下面给你【已有的过往总述】和【刚被裁出原文窗口的一段对话】。
请按时间顺序，把这段对话里发生的事写成若干行事件脉络，每行不超过 30 字，最多 4 行。
只写事件来龙去脉——谁应下什么、发生了什么转折、关系怎么变。
不要写外貌/动作/神态/心理/环境/对白原句；不要编号、不要解释，每行一条。
""";

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
            var cfgCopy = new LlmClient.Config { Url = cfg.Url, ApiKey = cfg.ApiKey, Model = cfg.Model };

            // ★ 2026-11-02 主人定（乙方案）：总述为空时，**先**把整段历史压成一份笼统目录。
            //   只在玩家点「立即整理」时发生，不自动跑、不在加载时偷拼。
            // ★ 2026-11-07：判断改读 RenderPastSummary()（分代后的渲染值）。
            var pastNote = "";
            if (string.IsNullOrWhiteSpace(mem.Skeleton.RenderPastSummary()))
            {
                var (okPs, msgPs) = BuildPastSummaryNow(mem, cfgCopy);
                pastNote = "；" + msgPs;
                if (okPs) MemoryStore.Save(mem);
            }

            var snapshot = TakeSnapshot(mem);
            var result = RunAsync(snapshot, cfgCopy, (m, w) => { }).GetAwaiter().GetResult();

            if (result)
                return (true, "整理完成（摘要/事实/骨架/过往总述已更新" + pastNote + "）");
            if (!string.IsNullOrWhiteSpace(mem.Skeleton.RenderPastSummary()))
                return (true, (pastNote.Length > 0 ? pastNote.TrimStart('；') + "；" : "") + "没有新对话要整理");
            return (false, "没有新的对话可整理（都已经整理过了）");
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
                // ★ 2026-11-07：这里放**渲染后的总述**（多代拼接），提示词里显示用
                PastSummary = mem.Skeleton.RenderPastSummary(),
                PastGens = new List<PastGen>(mem.Skeleton.PastGens ?? new List<PastGen>()),
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
            // ★ 2026-11-03：摘要句块老化（1000 条上限 / 500 轮未召回）
            var sumEvents = Aging.EvictSummaryChunks(mem, snap.NpcCfgId);
            if (sumEvents.Count > 0)
                log?.Invoke($"[老化] 摘要淘汰 {sumEvents.Count} 条（上限 {Aging.SummaryChunkCap} / {Aging.SummaryIdleTurns} 轮）", false);
            MemoryStore.Save(mem);
        }
        catch { }

        // ★ 内存缓存里的 LastExtractTurn 还是旧值 → 必须作废，否则会重复触发抽取
        MemoryEngine.InvalidateCache(snap.NpcCfgId);

        log?.Invoke(
            $"[抽取] 完成：关系+{addedRelations} 事实+{updatedFacts} 摘要句+{addedSummary}", false);
        return true;
    }

    // ================= 过往总述（乙方案：只由「立即整理」触发） =================

    /// <summary>
    /// ★ 2026-11-07 主人定（分层代 A 方案）：不再「整段历史压成 300 字」，
    ///   改成「只写最近 30 轮发生了什么，≤30 字」，由程序按代累积。
    ///
    /// 为什么改：原来把旧总述一起喂给模型让它「合并续写」，它经常只写最近的事 →
    ///   旧事被整体覆盖（实测 100111 的早期剧情全丢，总述只剩最近几轮）。
    /// 现在模型不用碰旧历史，只描述「这段发生了什么」，拼接/淘汰全由程序负责。
    /// </summary>
    private const string PastSummarySystem = """
你是记忆记录员。下面是你和玩家【最近这一段】的对话。

只写这段里【发生了什么事】——事件的起因、经过、结果。
不超过 30 字，直接写正文，不要编号、不要标题、不要解释。

【绝对不要写】
· 外貌、动作、神态、心理、环境、服装
· 对白的原句
· 情绪铺陈、氛围渲染

【例子】
✔「他应下替我炼压制丹药，头一粒我先试。」
✔「我们在客栈定下婚约，他要先办成喜事。」
✔「养母的真面目被揭穿，我与玩家联手将她击败。」
✘「他指尖划过我耳根，我耳根一热，声音都软了。」（带描写）
✘「次日他备好热水，我让他搁门口自己擦。」（是细节，不是事件）

只写材料里有的，不要编造。如果这段没有值得记的事，只输出空。
""";

    /// <summary>
    /// 生成总述用的材料。
    /// ★ 2026-11-07（分层代 A 方案）：**只取最近 30 轮原文**。
    ///   原来给整段历史（最多 24000 字）让模型压成 300 字，它经常只写最近的事、丢旧事；
    ///   现在模型只负责「这一段发生了什么」，旧的由程序分层累积。
    /// </summary>
    private static string PastSummaryInput(NpcMemory mem)
    {
        const int RecentRounds = 30;
        var sb = new StringBuilder();
        var dlg = mem.Chunks.Where(c => c.Kind == ChunkKind.Dialog)
            .OrderBy(c => c.Turn).ThenBy(c => c.Id).ToList();
        if (dlg.Count > 0)
        {
            var maxTurn = dlg[dlg.Count - 1].Turn;
            var minTurn = maxTurn - RecentRounds;
            var recent = dlg.Where(c => c.Turn > minTurn).ToList();
            sb.AppendLine("## 最近 " + RecentRounds + " 轮的对话（按【第N轮】升序）");
            foreach (var c in recent)
            {
                var t = (c.Text ?? "").Trim();
                if (t.Length == 0) continue;
                sb.AppendLine("【第" + c.Turn + "轮】" + t);
            }
            sb.AppendLine();
        }
        // ★ 2026-11-07：不再喂「近期提要」——模型只写这一段的事，旧的由程序管
        return sb.ToString();
    }

    /// <summary>同步把整段历史压成过往总述（玩家点「立即整理」时先跑这一步）。</summary>
    private static (bool Ok, string Msg) BuildPastSummaryNow(NpcMemory mem, LlmClient.Config cfg)
    {
        try
        {
            var input = PastSummaryInput(mem);
            if (string.IsNullOrWhiteSpace(input)) return (false, "没有可整理的历史");

            var (content, err) = LlmClient.CompleteAsync(cfg, PastSummarySystem, input, 1200)
                .GetAwaiter().GetResult();
            if (content == null) return (false, "总述生成失败（模型调用失败：" + err + "）");

            // ★ 2026-11-07（分层代 A 方案）：不再整体替换，改成追加一代（≤30 字）
            var rawText = content.Trim();
            if (string.IsNullOrWhiteSpace(rawText)) return (false, "模型没返回有效总述");
            var maxTurn = mem.Chunks.Count > 0 ? mem.Chunks.Max(c => c.Turn) : 0;
            var gen = mem.Skeleton.AddPastGen(rawText, maxTurn, out var evicted);
            if (gen == null) return (false, "模型没返回有效总述");
            var msg = "已新增第 " + gen.Gen + " 代过往总述（" + gen.Text.Length + " 字）";
            if (evicted != null) msg += "；淘汰了第 " + evicted.Gen + " 代";
            return (true, msg);
        }
        catch (Exception e)
        {
            return (false, "总述生成失败：" + e.Message);
        }
    }

    /// <summary>★ 2026-11-07 主人定（B 方案）：把旧格式的第 0 代总述压成 30 字，让风格跟新代统一。</summary>
    private const string CompressLegacySystem = """
你是记忆压缩员。下面是一段旧的记忆总述。
请压成不超过 30 字，只保留【最重要的事件】——谁应下什么、发生了什么转折、关系怎么变。
不要写外貌/动作/神态/心理/环境/对白原句；不要编号、不要解释，只输出压缩后的那一句。
""";

    /// <summary>
    /// ★ 2026-11-07（B 方案）：把第 0 代（迁移来的旧总述）压成 ≤30 字。
    /// 由控制台「压缩旧总述」按钮触发，手动一次、不自动跑。
    /// </summary>
    public static (bool Ok, string Msg) CompressLegacyPastGen(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return (false, "缺少角色 ID");

        NpcMemory mem;
        try { mem = MemoryStore.Load(npcCfgId); }
        catch (Exception e) { return (false, "读记忆失败：" + e.Message); }

        if (mem.Skeleton.PastGens == null || mem.Skeleton.PastGens.Count == 0)
            return (false, "没有总述可压（先聊几轮或点「立即整理」）");

        var oldest = mem.Skeleton.PastGens[0];
        if (oldest == null || string.IsNullOrWhiteSpace(oldest.Text))
            return (false, "第 0 代是空的，不用压");
        if (oldest.Text.Length <= Skeleton.GenCharLimit)
            return (false, $"第 0 代只有 {oldest.Text.Length} 字（上限 {Skeleton.GenCharLimit}），不需要压缩");

        var cfg = MemoryEngine.GetLlmConfig(null);
        if (!cfg.IsValid) return (false, "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey");

        var (content, err) = LlmClient.CompleteAsync(cfg, CompressLegacySystem, oldest.Text, 200)
            .GetAwaiter().GetResult();
        if (content == null) return (false, "压缩失败（模型调用失败：" + err + "）");

        var text = content.Trim().Replace("\r", " ").Replace("\n", " ");
        if (text.Length == 0) return (false, "模型没返回内容");
        if (text.Length > Skeleton.GenCharLimit) text = text.Substring(0, Skeleton.GenCharLimit);

        var before = oldest.Text.Length;
        oldest.Text = text;
        try { MemoryStore.Save(mem); }
        catch (Exception e) { return (false, "保存失败：" + e.Message); }

        return (true, $"第 {oldest.Gen} 代已压缩：{before} 字 → {text.Length} 字「{text}」");
    }

    // ================= Prompt =================

    private static string BuildSystemPrompt() =>
        """
你是记忆整理器。根据对话产出结构化记忆。只输出一个 JSON 对象，不要任何解释。

JSON 格式：
{
  "relations": ["新增的关系条目"],
  "situation": "当前处境一句话",
  "pastSummary": "最近这段发生了什么（事件来龙去脉，不超过 30 字，不写描写）",
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
  ★ 用第一人称写，不要写成「玩家与角色为道侣」这种第三人称：
    写成「我与他是道侣」「他认我做师父」「我欠他一条命」「他是我的恩人」。
    不写具体名字（名字会变，也会对不上），一律用「我 / 他」。
- ★ relations 与 facts 互斥：关系本身（谁是谁的师徒/道侣/亲友）只写进 relations，
  不要再把「玩家与角色为道侣/夫妻」「玩家为角色徒弟」这类关系事实写进 facts。
  facts 只写「关系之外」的里程碑（承诺、生死相托、名分之外的转折、影响关系走向的具体事件）。
- situation：当前处境一句话，会替换旧值。没有明显变化就给 ""。
- pastSummary：**最近这段对话发生了什么**（会作为「一代」存进骨架，程序负责拼接和淘汰旧代，不用你操心）。要求：
    · **只写这一段新发生的事**：事件的起因、经过、结果；不要重述旧历史；
    · **不超过 30 字**（硬上限，超了会被截断）；
    · 用她自己的视角与口吻写（第一人称「我」，对方「他/你」），不要用「玩家/角色」的第三人称报账说法；
    · 只写「发生了什么、结果如何、关系或约定有什么变化」；
      **绝对不写外貌/动作/神态/心理/环境/服装/对白原句/情绪铺陈**；情事只写结果（谁应下、谁定规矩、谁要什么），不写过程；
    · 直接写正文，不要分行、不要编号、不要解释；
    · 这段没有值得记的事就给 ""。
- summary：3~8 句，按时间顺序，每句一个完整事件，总字数不超过 500。没有就给 []。
    用她自己的视角与口吻（我 / 他），不要用「玩家/角色」这类第三人称报账说法。
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
     · ★ text 用她自己的视角写：第一人称「我」，对方称「他/你」；
       像她心里记着的一句话，不要写成「玩家承诺…/某角色定下…/玩家告知…」这种报账体
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
        sb.AppendLine("过往总述：" + (string.IsNullOrWhiteSpace(snap.Skeleton.PastSummary)
            ? "（无）" : snap.Skeleton.PastSummary));
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
        public string PastSummary = "";
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

            // ★ 2026-11-02：过往总述（兼容 past_summary 写法）
            if (root.TryGetProperty("pastSummary", out var ps) && ps.ValueKind == JsonValueKind.String)
                p.PastSummary = ps.GetString()?.Trim() ?? "";
            else if (root.TryGetProperty("past_summary", out var ps2) && ps2.ValueKind == JsonValueKind.String)
                p.PastSummary = ps2.GetString()?.Trim() ?? "";

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

        // ★ 2026-11-07 主人定（分层代 A 方案）：过往总述改成「追加一代」。
        //   · 模型只写「这一段发生了什么」（≤30 字，程序硬截断）
        //   · 保留最近 Skeleton.MaxPastGens(10) 代，超了淘汰最老
        //   · 旧数据（PastSummary 单段）已由 MemoryStore.ParseSkeleton 迁成第 0 代
        var newPast = (p.PastSummary ?? "").Trim();
        if (newPast.Length == 0)
        {
            log?.Invoke("[抽取] pastSummary 为空 → 不新增代（旧代保留）", false);
        }
        else
        {
            var segTurnForPast = todo.Count > 0 ? todo[^1].Turn : 0;
            var gen = mem.Skeleton.AddPastGen(newPast, segTurnForPast, out var evicted);
            if (gen != null)
            {
                var clipped = newPast.Length > Skeleton.GenCharLimit;
                log?.Invoke($"[抽取] 新增第 {gen.Gen} 代总述（{gen.Text.Length} 字{(clipped ? "，已截断" : "")}）：「{gen.Text}」", false);
            }
            if (evicted != null)
            {
                var brief = evicted.Text.Length > 20 ? evicted.Text.Substring(0, 20) : evicted.Text;
                log?.Invoke($"[抽取] 淘汰第 {evicted.Gen} 代（超过 {Skeleton.MaxPastGens} 代上限）：「{brief}」", false);
            }
        }

        // ★ 2026-11-03：改成**追加**一段（恢复设计：摘要段不合并、不覆盖、不滚动）
        //   段号 = 本次覆盖的最后一轮对话（重抽同一批 → 同段号 → 幂等）
        var segTurn = todo.Count > 0 ? todo[^1].Turn : 0;
        var addedSummary = MemoryStore.AppendSummarySegment(mem, p.Summary, segTurn, 500);

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
