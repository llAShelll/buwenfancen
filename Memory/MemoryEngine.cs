using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆引擎协调层：把「数据模型 + 读写 + 老化」接进游戏 hook。
/// ① 注入保底层：骨架 + 4~5 级事实 → 拼进游戏 memory 参数
/// ② 写入 chunk：每轮对话（玩家话 + 角色话）写成一条 dialog 块
/// 用内存缓存，避免每轮都全量 Load/Save。
/// </summary>
public static class MemoryEngine
{
    private static readonly Dictionary<string, NpcMemory> _cache = new();
    private static readonly object _lock = new();

    /// <summary>日志输出委托（由 Plugin 注入 BepInEx 日志；本地测试时为 null）</summary>
    public static Action<string, bool> Log;

    private static void WriteLog(string msg, bool warn = false)
    {
        try { Log?.Invoke(msg, warn); } catch { }
    }

    /// <summary>取（或加载）某 NPC 的记忆，带缓存。</summary>
    public static NpcMemory Get(string npcCfgId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(npcCfgId, out var m)) return m;
            m = MemoryStore.Load(npcCfgId);
            _cache[npcCfgId] = m;
            return m;
        }
    }

    /// <summary>作废内存缓存（抽取流程在后台写盘后调用，强制下次重新加载）。</summary>
    public static void InvalidateCache(string npcCfgId)
    {
        lock (_lock) _cache.Remove(npcCfgId ?? "");
    }

    private static bool _cfgIdDebugged;

    /// <summary>从 NpcModel 反射读出稳定 ID（NpcCfgId.Value）。</summary>
    public static string GetNpcCfgId(object npc)
    {
        try
        {
            var cfgId = ReadProp(npc, "NpcCfgId");
            if (cfgId == null)
            {
                if (!_cfgIdDebugged)
                {
                    _cfgIdDebugged = true;
                    WriteLog($"[记忆] NpcCfgId 属性读不到，npc 类型={npc?.GetType().FullName}", true);
                }
                return "";
            }
            var value = ReadProp(cfgId, "Value");
            if (value == null)
            {
                if (!_cfgIdDebugged)
                {
                    _cfgIdDebugged = true;
                    WriteLog($"[记忆] NpcCfgId.Value 读不到，cfgId 类型={cfgId.GetType().FullName}，ToString={cfgId}", true);
                }
                return cfgId.ToString() ?? "";
            }
            if (!_cfgIdDebugged)
            {
                _cfgIdDebugged = true;
                WriteLog($"[记忆] NpcCfgId 读取成功 = {value}");
            }
            return value.ToString() ?? "";
        }
        catch (Exception e)
        {
            if (!_cfgIdDebugged)
            {
                _cfgIdDebugged = true;
                WriteLog($"[记忆] NpcCfgId 读取异常：{e.GetType().Name}: {e.Message}", true);
            }
            return "";
        }
    }

    /// <summary>
    /// 角色名。
    ///
    /// ★ 2026-10-25 主人定：优先用【初始角色名】BaseName，不要皮肤名 NpcName。
    ///   实测 NpcName 会随皮肤变（「某某·某角色」），BaseName 才是本名。
    ///   拿不到 BaseName 才退回 NpcName。
    /// </summary>
    /// <summary>
    /// 按 cfgId 解析「真实角色名」：优先配置表初始名 → 退回 meta.json 里存的。
    /// 2026-10-25：所有显示/存储层统一走这里，避免有的地方还显示「某某·XX」。
    /// </summary>
    public static string ResolveName(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return "";
        try
        {
            var fromTable = ConfigTable.GetBaseName(npcCfgId);
            if (!string.IsNullOrWhiteSpace(fromTable)) return fromTable;
        }
        catch { }
        try
        {
            return MemoryStore.Load(npcCfgId).Meta.NpcName ?? "";
        }
        catch { return ""; }
    }

    public static string GetNpcName(object npc)
    {
        // ① 配置表里的初始名（最准，2026-10-25 探针查明入口是 LubanDatas.Tables）
        try
        {
            var cfgId = GetNpcCfgId(npc);
            var fromTable = ConfigTable.GetBaseName(cfgId);
            if (!string.IsNullOrWhiteSpace(fromTable)) return fromTable;
        }
        catch { }

        // ② NpcModel 上的 BaseName（当前版本没有这个属性，留着兼容）
        try
        {
            var baseName = ReadProp(npc, "BaseName")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(baseName)) return baseName.Trim();
        }
        catch { }

        // ③ NpcName（会带皮肤前缀，如「某某·某角色1」）
        try
        {
            return ReadProp(npc, "NpcName")?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ================= 跨 hook 的「待写入」缓冲 =================
    // CompleteBudgeted（请求侧）记录「玩家说了什么」，
    // AddNpcChatMessage（响应侧）补上「角色回了什么」后一起写成 chunk。
    private static string _pendingCfgId;
    private static string _pendingNpcName;
    private static string _pendingUserText;

    /// <summary>请求侧：记录本轮玩家说的话（等待响应侧一起落盘）。</summary>
    public static void RecordTurn(object npc, string userText)
    {
        _pendingCfgId = GetNpcCfgId(npc);
        _pendingNpcName = GetNpcName(npc);
        _pendingUserText = userText ?? "";

        // ★ 2026-10-26：拿到不角色 ID 时记一条——「对话不写」到底断在请求侧还是响应侧，靠它区分。
        if (string.IsNullOrEmpty(_pendingCfgId) && !_recNoIdLogged)
        {
            _recNoIdLogged = true;
            try
            {
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogWarning("[记忆] 请求侧拿不到角色 ID（GetNpcCfgId 失败）→ 本轮对话不会入库。"
                              + "如果每轮都这样，把这条日志发给作者。");
            }
            catch { }
        }
    }

    private static bool _recNoIdLogged;

    /// <summary>窥探当前待写入的玩家话（不消费），供输出侧判断好感用。</summary>
    public static (string CfgId, string User) PeekPending()
    {
        return (_pendingCfgId, _pendingUserText);
    }

    /// <summary>响应侧：补上角色回复，写成一条 dialog chunk。</summary>
    public static void FlushTurn(string assistantText, int turn)
    {
        if (string.IsNullOrEmpty(_pendingCfgId))
        {
            // ★ 2026-10-26：请求侧没记录 → 本轮不会入库。打一次警告，方便玩家/作者定位。
            if (!_noPendingLogged)
            {
                _noPendingLogged = true;
                try
                {
                    BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                        .LogWarning("[记忆] 没有待写入的玩家话（请求侧没记录）→ 本轮对话不入库。"
                                  + "如果每轮都这样，说明注入 hook 没命中本轮请求。");
                }
                catch { }
            }
            return;
        }
        var cfgId = _pendingCfgId;
        var name = _pendingNpcName;
        var user = _pendingUserText;
        // 先清空，防重复写入
        _pendingCfgId = null;
        _pendingNpcName = null;
        _pendingUserText = null;
        WriteTurn(cfgId, name, user, assistantText ?? "", turn);
    }

    private static bool _noPendingLogged;

    /// <summary>
    /// ① 注入保底层：骨架 + 4~5 级事实；② 命中层：BM25 检索的相关回忆。
    /// query = 玩家这句话（用于检索）；为空则只注入保底层。
    /// </summary>
    public static string BuildMemoryBlock(NpcMemory mem, string query = null)
    {
        if (mem == null) return "";
        var sb = new StringBuilder();
        var hasContent = false;

        if (mem.Skeleton.Relations.Count > 0)
        {
            sb.AppendLine("## 长期记忆");
            sb.AppendLine("- 关系：" + string.Join("；", mem.Skeleton.Relations));
            hasContent = true;
        }
        if (!string.IsNullOrEmpty(mem.Skeleton.Situation))
        {
            if (!hasContent) sb.AppendLine("## 长期记忆");
            sb.AppendLine("- 处境：" + mem.Skeleton.Situation);
            hasContent = true;
        }
        var keyFacts = mem.Facts.Where(f => f.Importance >= 4).ToList();
        // ★ 2026-10-27：去掉和骨架【关系】重复的 4~5 级事实
        //   （抽取时模型可能把「玩家与角色为道侣」既写进 relations、又写成 5 级 facts，
        //     两处都注入 = 同一事实说两遍，浪费 token 还让模型过度强调）
        if (keyFacts.Count > 0 && mem.Skeleton.Relations.Count > 0)
        {
            var kept = new List<Fact>();
            foreach (var f in keyFacts)
                if (!OverlapsAny(f.Text, mem.Skeleton.Relations)) kept.Add(f);
            if (kept.Count != keyFacts.Count)
            {
                try
                {
                    Log?.Invoke($"[记忆] 注入去重：跳过 {keyFacts.Count - kept.Count} 条与【关系】重复的事实", false);
                }
                catch { }
            }
            keyFacts = kept;
        }
        if (keyFacts.Count > 0)
        {
            if (!hasContent) sb.AppendLine("## 长期记忆");
            sb.AppendLine("- 关键事实：");
            foreach (var f in keyFacts)
                sb.AppendLine($"  · {f.Text}（{f.Importance} 级）");
            hasContent = true;
        }

        // ★ 阶段 3：命中层（检索相关回忆）
        if (!string.IsNullOrEmpty(query) && (mem.Chunks.Count > 0 || mem.Facts.Count > 0))
        {
            try
            {
                var hits = MemoryRetriever.Search(mem, query, 8);
                // ★ 2026-10-26：把「开场白抄本」从注入里剔掉。
                //   模型复述开场白时会被写成 dialog chunk，之后每次检索都把它注回上下文，
                //   表现就是「开场白都删了，模型还在反复提开场白里的东西」。
                try
                {
                    var opening = BuWenFanChen.GreetingFilter.GetKnownOpening(mem.Meta.NpcCfgId);
                    if (!string.IsNullOrEmpty(opening) && hits.Count > 0)
                    {
                        var probe = opening.Length > 20 ? opening.Substring(0, 20) : opening;
                        hits = hits.Where(h => h.Chunk == null || !(h.Chunk.Text ?? "").Contains(probe)).ToList();
                    }
                }
                catch { }
                var recall = MemoryRetriever.BuildRecallBlock(hits, 800);
                if (!string.IsNullOrEmpty(recall))
                {
                    sb.AppendLine();
                    sb.Append(recall);
                    hasContent = true;
                }
            }
            catch
            {
                // 检索失败不影响保底层注入
            }
        }

        if (!hasContent) return "";

        // ★ 2026-10-25：按记忆档位截断（对齐 1.1.2）
        //   lite = 2000 字 / full = 3000 字
        var text = sb.ToString().TrimEnd();
        var limit = PromptConfig.MemoryInjectLimit;
        if (text.Length > limit)
        {
            text = text.Substring(0, limit) + "\n…（记忆已按当前档位截断）";
        }
        return text;
    }

    /// <summary>这条事实是不是主要在说「关系」（和骨架某条 relation 高度重合）。</summary>
    private static bool OverlapsAny(string factText, List<string> relations)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(factText) || relations == null) return false;
            foreach (var r in relations)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                if (BigramOverlap(r, factText) >= 0.6) return true;
            }
        }
        catch { }
        return false;
    }

    private static double BigramOverlap(string a, string b)
    {
        var sa = Bigrams(a);
        var sb = Bigrams(b);
        if (sa.Count == 0 || sb.Count == 0) return 0;
        var inter = 0;
        foreach (var x in sa) if (sb.Contains(x)) inter++;
        return (double)inter / Math.Min(sa.Count, sb.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(s)) return set;
        var t = new string(s.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray());
        for (int i = 0; i + 1 < t.Length; i++) set.Add(t.Substring(i, 2));
        return set;
    }

    /// <summary>
    /// ② 写入一轮对话 chunk（玩家话 + 角色话）。
    /// 每 48 轮触发一次老化（对应抽取间隔，阶段 3 抽取接入前先在这里兜底）。
    /// </summary>
    public static void WriteTurn(string npcCfgId, string npcName, string userText, string assistantText, int turn)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return;
        var mem = Get(npcCfgId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ★ 2026-10-26：游戏传的 turn 在很多场景恒为 0（实测玩家端全是「第 0 轮」）→
        //   用「已写入的对话条数 + 1」自增，保证每条对话都有轮次。
        //   控制台的「第 N 轮」显示、老化按轮次触发，都依赖它。
        var turnNo = turn > 0 ? turn : mem.Chunks.Count(c => c.Kind == ChunkKind.Dialog) + 1;

        var chunk = new Chunk
        {
            Kind = ChunkKind.Dialog,
            Npc = string.IsNullOrEmpty(npcName) ? npcCfgId : npcName,
            Text = $"玩家：{userText}\n角色：{assistantText}",
            Turn = turnNo,
            CreatedAt = now,
            LastSeen = now,
        };

        lock (_lock)
        {
            MemoryStore.AppendChunk(mem, chunk);
            try
            {
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogInfo($"[记忆] 写入对话 #{turnNo}（玩家{userText?.Length ?? 0}字 + 角色{assistantText?.Length ?? 0}字，累计 {mem.Chunks.Count(c => c.Kind == ChunkKind.Dialog)} 条）");
            }
            catch { }
            MemoryRetriever.Invalidate(npcCfgId);   // 新块入库，检索缓存作废
            mem.Meta.NpcName = npcName;
            MemoryStore.Save(mem);

            // 每 48 条对话老化一次（先兜底，抽取接入后移到抽取流程）
            if (turnNo > 0 && turnNo % 48 == 0)
            {
                Aging.AgeFacts(mem, npcCfgId);
                Aging.EnforceLevel4Cap(mem, npcCfgId);
                Aging.EvictDialogChunks(mem, npcCfgId);
                MemoryStore.Save(mem);
            }
        }
    }

    private static object ReadProp(object obj, string name)
    {
        try
        {
            return obj?.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拿玩家在游戏里配好的 API 配置。
    /// ★ 优先读 settings.json 的 ainpc 段（最可靠；runtime.Settings 在 Harmony Prefix 里拿不到实例）。
    /// 兜底才是从 runtime.Settings 反射读。
    /// </summary>
    public static LlmClient.Config GetLlmConfig(object runtime)
    {
        // ★ 2026-10-25 主人要：MOD 的所有 API 调用单独走，不和游戏对话共用
        //   填了 modApiUrl + modApiKey 就用 MOD 自己的；没填才退回游戏的 settings.json
        try
        {
            if (!string.IsNullOrWhiteSpace(PromptConfig.ModApiUrl) &&
                !string.IsNullOrWhiteSpace(PromptConfig.ModApiKey))
            {
                Log?.Invoke($"[配置] 用 MOD 独立 API：url={PromptConfig.ModApiUrl} model={PromptConfig.ModModel}", false);
                return new LlmClient.Config
                {
                    Url = PromptConfig.ModApiUrl,
                    ApiKey = PromptConfig.ModApiKey,
                    Model = string.IsNullOrWhiteSpace(PromptConfig.ModModel) ? "" : PromptConfig.ModModel,
                };
            }
        }
        catch { }

        // ① 优先：读 settings.json
        try
        {
            var settingsPath = Path.Combine(
                Path.GetDirectoryName(MemoryPaths.Root) ?? "", "settings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("ainpc", out var ainpc))
                {
                    var url = ainpc.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var key = ainpc.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
                    var model = ainpc.TryGetProperty("model", out var m) ? m.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
                    {
                        Log?.Invoke($"[配置] 从 settings.json 读取：url={url} model={model} keyLen={key.Length}", false);
                        return new LlmClient.Config { Url = LlmClient.NormalizeUrl(url), ApiKey = key, Model = model ?? "" };
                    }
                    Log?.Invoke($"[配置] settings.json 的 ainpc 段不完整（url/key 空）", true);
                }
                else
                {
                    Log?.Invoke($"[配置] settings.json 没有 ainpc 段", true);
                }
            }
            else
            {
                Log?.Invoke($"[配置] settings.json 不存在：{settingsPath}", true);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"[配置] settings.json 读取异常: {e.GetType().Name}: {e.Message}", true);
        }

        // ② 兜底：从 runtime.Settings 反射读
        try
        {
            if (runtime == null) return null;
            var settings = ReadProp(runtime, "Settings");
            if (settings == null) return null;
            var url = ReadProp(settings, "ResolvedUrl")?.ToString();
            var key = ReadProp(settings, "ApiKey")?.ToString();
            var model = ReadProp(settings, "Model")?.ToString();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return null;
            Log?.Invoke($"[配置] 从 runtime.Settings 读取（兜底）", false);
            return new LlmClient.Config { Url = LlmClient.NormalizeUrl(url), ApiKey = key, Model = model ?? "" };
        }
        catch
        {
            return null;
        }
    }
}
