using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Card;

/// <summary>
/// 角色卡自动生成（阶段 5-B）。
///
/// 玩家在控制台粘贴「官方角色资料」（从游戏里复制，或从 dump 拿），
/// 这里调模型产出一张符合格式的角色卡 JSON。
///
/// 提示词照搬 1.1.2 的 _card_generate_prompt（去掉「真实说话样本」部分，
/// 因为 V1.2 这边拿不到稳定的历史样本）。
/// </summary>
public static class CardGenerator
{
    private const string SystemPrompt = @"你是角色设定分析师。根据下面提供的【官方角色资料】，为这个角色产出一份「角色卡」，
只输出严格 JSON（不要多余文字、不要 markdown 代码块）：

{""口吻"":{""语气基调"":"""",""句长节奏"":"""",""旁白比例"":"""",""情绪外露度"":"""",""自称"":"""",""称谓"":""0 陌生=…；1 相识=…；2 友善=…；3 知己=…；4 莫逆=…；5 缘契=…"",""语言底色"":"""",""标点与句尾"":"""",""口头禅语气词"":[],""用词比喻取材"":"""",""惯用手势/小动作"":[],""绝不说"":[],""日常相处梯度"":""0 陌生=…；1 相识=…；2 友善=…；3 知己=…；4 莫逆=…；5 缘契=…"",""行为逻辑设定"":"""",""感官标识"":""""},""NSFW"":{""身体特征"":"""",""敏感带"":[],""反应特征"":"""",""性经验与态度"":"""",""性癖偏好"":[],""语言风格"":"""",""节奏偏好"":"""",""情事用语尺度"":"""",""性爱行为设定"":"""",""亲密举动解锁节奏"":""""}}

要求：
- 口吻的每一条**不超过 30 字**，要具体到能直接指导怎么写台词，不写空话套话。
- 口吻只能来自官方资料与合理推断，资料没写的不要编造重大设定。
- 「自称」按场合分档写：对陌生人／日常／只有两人时／生气时，各自怎么自称。
- 「称谓」只写【她怎么称呼对方】，按好感度六档写，每行一档：0 陌生=…／1 相识=…／2 友善=…／3 知己=…／4 莫逆=…／5 缘契=…。
- 惯用手势/小动作、口头禅语气词、绝不说、敏感带、性癖偏好 一律用数组，各 3~5 项。
- 「行为逻辑设定」写成多行文本，每行一条：当「什么情况」→ 则「她怎么反应」，3~5 条。
- 「感官标识」要写成**外观硬指标**：惯常穿着（衣服、鞋袜）、饰物、气味、标志性小动作、面部特征等，尽量具体可落地。它会被当作「凡涉及外貌与衣着描写都必须与此一致」的强制设定使用，写得含糊等于没用。
- 「绝不说」是**文风红线**，必须写这个角色绝不会出现的**腔调与说法**（例如：甜腻撒娇、叠词、「人家」、文绉绉书面语、网络词、拿腔拿调的官腔），用途是防止所有角色都被写成同一种腔调；**不要**写成价值观或人生信条。
- 日常相处梯度按好感度等级分档写，每行一档，写【日常相处】中随关系变化的具体表现，不要写好感数值。
- NSFW 部分按角色性格与身份克制地推断，贴合人物；拿不准的宁可写保守。
- 「性爱行为设定」写成多行文本，每行一条 当「…」→ 则「…」，给 3 条（刚进入 / 中途 / 结束后）。
- 「情事用语尺度」写成这个角色在亲密场景里的用语基准：从「含蓄文艺 / 半推半就 / 直白粗俗 / 极致下流」里选一档并展开。**默认不要低于「直白粗俗」**；性格羞怯的可写「半推半就转直白粗俗」。
- 「标点与句尾」只写标点上的**个人习惯**（爱用破折号、省略号多不多、句尾常带什么语气词）；**不要写引号样式**——那是全局统一规则。
- 全部使用简体中文。

只输出 JSON，不要任何解释。";

    /// <summary>
    /// 根据官方资料生成一张卡。material 就是玩家粘贴的设定文本。
    /// 返回 (成功, JSON文本 或 错误信息)。
    /// </summary>
    public static async Task<(bool Ok, string Data)> GenerateAsync(string npcCfgId, string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return (false, "请先粘贴官方角色资料");

        var cfg = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
        // ★ 角色卡专用模型（config.json 的 cardModel，空则沿用游戏模型）
        if (cfg != null && !string.IsNullOrWhiteSpace(PromptConfig.CardModel))
            cfg.Model = PromptConfig.CardModel;
        if (!cfg.IsValid) return (false, "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey");

        if (material.Length > 8000) material = material.Substring(0, 8000);

        var user = "【官方角色资料】\n" + material;
        var (content, err) = await BuWenFanChen.Memory.LlmClient
            .CompleteAsync(cfg, SystemPrompt, user, 4000);

        if (content == null) return (false, "模型调用失败：" + err);

        // 剥掉可能的 markdown 代码块
        var t = content.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
            t = t.Trim();
        }

        // 只取最外层 JSON
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start) return (false, "模型没有返回 JSON：" + Truncate(content, 150));
        t = t.Substring(start, end - start + 1);

        // 校验能不能解析
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(t) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return (false, "返回的不是 JSON 对象");
            // 补上「启用」字段（默认开）和 _来源
            if (!node.ContainsKey("启用")) node["启用"] = true;
            if (!node.ContainsKey("_来源"))
            {
                var src = new System.Text.Json.Nodes.JsonObject
                {
                    ["说明"] = "由控制台生成，仅作底稿，不注入模型",
                    ["生成时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["挂载角色"] = npcCfgId ?? "",
                };
                node["_来源"] = src;
            }
            return (true, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception e)
        {
            return (false, "返回的 JSON 解析失败：" + e.Message);
        }
    }

    // ==================== 按人设原稿重写（阶段 5-D）====================

    private const string RewriteSystem = @"你是角色设定分析师。下面给你两份材料：
① 【AI生卡】—— 玩家亲手写的设定，这是最高依据，与任何东西冲突都以它为准
② 【现有角色卡】—— 之前生成的卡，格式是对的，但可能和原稿不符

请按【AI生卡】把角色卡重写一遍，保持完全相同的 JSON 结构（键名不能变）。

要求：
- 原稿里写明的设定（身份、关系、性格、口吻、禁忌、亲密偏好等），一律以原稿为准改过来
- 原稿没提到的字段，尽量保留现有卡的内容，不要凭空删掉
- 所有字段仍然要【具体可执行】：不写「适当」「自然」「偶尔」这类含糊词，
  要写成模型能照着演的具体表现
- 「绝不说」写这个角色绝不会出现的腔调（防止所有角色一个味儿），不是价值观
- 全部简体中文
- 只输出 JSON，不要任何解释、不要 markdown 代码块";

    /// <summary>读「AI生卡」字段（兼容旧卡字段名「人设原稿」）</summary>
    public static string GetDraft(JsonObject card)
    {
        try
        {
            if (card["人设"] is not JsonObject ren) return "";
            if (ren.TryGetPropertyValue("AI生卡", out var v1) && v1 != null)
            {
                var s1 = v1.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(s1)) return s1;
            }
            if (ren.TryGetPropertyValue("人设原稿", out var v2) && v2 != null)
                return v2.GetValue<string>() ?? "";
            return "";
        }
        catch { return ""; }
    }

    /// <summary>把 JsonNode 归一化成字符串（diff 用；list 排序后比较，顺序不影响）</summary>
    private static string Normalize(JsonNode n)
    {
        if (n == null) return "";
        if (n is JsonArray arr)
        {
            var items = new List<string>();
            foreach (var x in arr) items.Add(Normalize(x));
            items.Sort(StringComparer.Ordinal);
            return "[" + string.Join(",", items) + "]";
        }
        if (n is JsonObject o) return o.ToJsonString();
        return (n.GetValue<string>() ?? n.ToJsonString());
    }

    /// <summary>
    /// 按「AI生卡」重写整卡，返回逐项差异（不直接覆盖，玩家勾选后应用）。
    /// 结果：{ 成功, 项:[{区块,字段,现在,新值,手改}], 新卡, 可更新数, 手改数 }
    /// 「手改」= 玩家在上次 AI 生成之后又改过这个字段（用 _aiBaseline 基准卡 diff）。
    /// </summary>
    public static async Task<(bool Ok, JsonObject Data)> RewritePreviewAsync(string npcCfgId)
    {
        var card = CardStore.Load(npcCfgId);
        if (card == null) return (false, Err("这个角色还没有角色卡"));

        var draft = GetDraft(card);
        if (string.IsNullOrWhiteSpace(draft))
            return (false, Err("这张卡的「AI生卡」是空的，没有可依据的东西"));

        var cfg = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
        if (!cfg.IsValid) return (false, Err("读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey"));

        var current = card.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var user = "【AI生卡】\n" + draft + "\n\n【现有角色卡】\n" + current;
        if (user.Length > 16000) user = user.Substring(0, 16000);

        var (content, err) = await BuWenFanChen.Memory.LlmClient
            .CompleteAsync(cfg, RewriteSystem, user, 4500);
        if (content == null) return (false, Err("模型调用失败：" + err));

        var t = content.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
            t = t.Trim();
        }
        var a = t.IndexOf('{');
        var z = t.LastIndexOf('}');
        if (a < 0 || z <= a) return (false, Err("模型没有返回 JSON：" + Truncate(content, 150)));
        t = t.Substring(a, z - a + 1);

        JsonObject newNode;
        try { newNode = System.Text.Json.Nodes.JsonNode.Parse(t) as JsonObject; }
        catch { return (false, Err("返回的不是 JSON 对象")); }
        if (newNode == null) return (false, Err("返回的不是 JSON 对象"));

        // 保留 启用 / AI生卡 / _来源（重写不该丢掉玩家写的这段）
        if (card.ContainsKey("启用")) newNode["启用"] = System.Text.Json.Nodes.JsonNode.Parse(card["启用"]!.ToJsonString());
        else newNode["启用"] = true;
        newNode["人设"] ??= new JsonObject();
        (newNode["人设"] as JsonObject)!["AI生卡"] = draft;
        if (card["_来源"] != null) newNode["_来源"] = System.Text.Json.Nodes.JsonNode.Parse(card["_来源"]!.ToJsonString());

        // diff：只有「现在 != 新值」的字段才进列表；AI生卡/底线禁忌永远不改
        var baseline = card["_aiBaseline"] as JsonObject;
        var items = new System.Text.Json.Nodes.JsonArray();
        var handCount = 0;
        foreach (var sec in CardFields.Sections)
        {
            var curSec = card[sec] as JsonObject;
            var newSec = newNode[sec] as JsonObject;
            var baseSec = baseline?[sec] as JsonObject;
            foreach (var f in CardFields.Table[sec])
            {
                if (f.Name == "AI生卡" || f.Name == "人设原稿" || f.Name == "底线禁忌") continue;
                var curVal = Normalize(curSec?[f.Name]);
                var newVal = Normalize(newSec?[f.Name]);
                if (curVal == newVal) continue;
                var baseVal = Normalize(baseSec?[f.Name]);
                var hand = baseVal != "" && curVal != baseVal;
                if (hand) handCount++;
                items.Add(new JsonObject
                {
                    ["区块"] = sec,
                    ["字段"] = f.Name,
                    ["现在"] = curVal,
                    ["新值"] = newVal,
                    ["手改"] = hand,
                });
            }
        }

        var result = new JsonObject
        {
            ["成功"] = true,
            ["项"] = items,
            ["新卡"] = newNode,
            ["可更新数"] = items.Count,
            ["手改数"] = handCount,
        };
        return (true, result);
    }

    /// <summary>
    /// 应用玩家勾选的替换项。adoptedKeys 是 ["区块·字段", ...]。
    /// 应用后把新卡存成 _aiBaseline（下次 diff 以这次为准）。旧卡自动备份。
    /// </summary>
    public static (bool Ok, string Msg) ApplyRewrite(string npcCfgId, JsonObject newCard, JsonArray adoptedKeys)
    {
        var card = CardStore.Load(npcCfgId);
        if (card == null) return (false, "这个角色还没有角色卡");

        try
        {
            var adoptSet = new HashSet<string>();
            foreach (var k in adoptedKeys) adoptSet.Add(k?.GetValue<string>() ?? "");

            foreach (var sec in CardFields.Sections)
            {
                var newSec = newCard[sec] as JsonObject;
                if (newSec == null) continue;
                var curSec = card[sec] as JsonObject;
                if (curSec == null) { curSec = new JsonObject(); card[sec] = curSec; }
                foreach (var f in CardFields.Table[sec])
                {
                    var key = sec + "·" + f.Name;
                    if (!adoptSet.Contains(key)) continue;
                    if (newSec[f.Name] == null) continue;
                    curSec[f.Name] = System.Text.Json.Nodes.JsonNode.Parse(newSec[f.Name].ToJsonString());
                }
            }

            // 更新基准卡（下次 diff 以这次为准）
            card["_aiBaseline"] = System.Text.Json.Nodes.JsonNode.Parse(newCard.ToJsonString());

            // 备份旧卡
            try
            {
                var p0 = CardStore.PathOf(npcCfgId);
                if (System.IO.File.Exists(p0))
                    System.IO.File.Copy(p0, p0 + ".重写前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
            }
            catch { }

            return CardStore.Save(npcCfgId, card)
                ? (true, "已应用 " + adoptSet.Count + " 项")
                : (false, "保存失败");
        }
        catch (Exception e)
        {
            return (false, "应用失败：" + e.Message);
        }
    }

    private static JsonObject Err(string msg) => new JsonObject { ["成功"] = false, ["消息"] = msg };

    // ==================== 动态试演（2026-10-25 主人要）====================

    /// <summary>试演场景（照抄 1.1.2）</summary>
    public static readonly (string Name, string Desc)[] Scenes = new[]
    {
        ("初见（陌生）", "你们第一次见面。玩家上前搭话。"),
        ("日常闲聊", "平常日子里，玩家随口问你今天做了什么。"),
        ("被调笑", "玩家开了个带颜色的玩笑，故意逗你。"),
        ("被质疑本事", "玩家说你也就这点能耐，话里带刺。"),
        ("被保护/护短", "有人为难你，玩家站出来替你挡了。"),
        ("被赖账/被冒犯", "玩家提起你答应过的事，语气不善。"),
        ("收到礼物", "玩家送了你一件东西，说不值钱但挑了很久。"),
        ("为难的要求", "玩家提了一个你不太愿意答应的要求。"),
        ("好感高·亲昵", "玩家牵住你的手，凑到耳边说想你了。"),
        ("玩家说伤人的话", "玩家说了一句很伤人的话，转身要走。"),
        ("关系里程碑", "玩家正式向你提出结为道侣。"),
        ("亲密场景", "夜深人静，玩家抱住你，解你的衣带。"),
    };

    /// <summary>
    /// 动态试演：用当前这张卡，当场演一段（不进游戏就能验收）。
    /// 返回 (成功, 正文, 情绪标签, 错误/说明)
    /// </summary>
    public static async Task<(bool Ok, string Text, string Emotion, string Msg)> PreviewAsync(
        string npcCfgId, int sceneIdx, string model, bool nsfw, string extra)
    {
        var card = CardStore.Load(npcCfgId);
        if (card == null) return (false, "", "", "读不到这张角色卡");

        var cfg = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
        if (!cfg.IsValid) return (false, "", "", "读不到 API 配置：settings.json 的 ainpc 段缺 url 或 apiKey");
        if (!string.IsNullOrWhiteSpace(model))
            cfg = new BuWenFanChen.Memory.LlmClient.Config
            {
                Url = cfg.Url,
                ApiKey = cfg.ApiKey,
                Model = model.Trim(),
            };

        // 拼 system：角色基础信息 + 角色卡渲染 + 输出格式
        var sb = new StringBuilder();
        sb.AppendLine("你正在扮演一个角色。请严格按下面给出的角色设定与说话方式来回应玩家。");
        sb.AppendLine();

        if (card["_来源"] is JsonObject src)
        {
            foreach (var key in new[] { "角色名", "身份", "性格", "外貌", "背景" })
            {
                var v = src[key]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine("【" + key + "】" + v);
            }
            sb.AppendLine();
        }

        var cardBlock = CardRenderer.BuildBlock(card, nsfw);
        if (!string.IsNullOrWhiteSpace(cardBlock)) sb.AppendLine(cardBlock);

        sb.AppendLine();
        sb.AppendLine("【输出格式】只输出严格 JSON，不要任何多余文字：");
        sb.AppendLine("{\"content\":\"（旁白/动作）\\n“台词”\",\"emotion\":\"normal|happy|smile|think|sad|surprise|angry|afraid|doubt\",\"control\":\"none\"}");

        var scene = (sceneIdx >= 0 && sceneIdx < Scenes.Length) ? Scenes[sceneIdx] : Scenes[1];
        var user = "【场景】" + scene.Desc + "\n"
                 + (string.IsNullOrWhiteSpace(extra) ? "" : "【玩家说】" + extra.Trim() + "\n")
                 + "请直接给出你的回应（JSON）。";

        var (content, err) = await BuWenFanChen.Memory.LlmClient
            .CompleteAsync(cfg, sb.ToString(), user, 3000);
        if (content == null) return (false, "", "", "调用失败：" + err);

        // 解析 JSON
        var t = content.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
            t = t.Trim();
        }
        var a = t.IndexOf('{');
        var z = t.LastIndexOf('}');
        string text = content;
        string emotion = "";
        string note = "";
        if (a >= 0 && z > a)
        {
            try
            {
                var node = JsonNode.Parse(t.Substring(a, z - a + 1)) as JsonObject;
                text = node?["content"]?.GetValue<string>() ?? content;
                emotion = node?["emotion"]?.GetValue<string>() ?? "";
            }
            catch
            {
                note = "（模型没按 JSON 输出，正文已尽量提取）";
            }
        }
        else
        {
            note = "（模型没按 JSON 输出，正文已尽量提取）";
        }
        return (true, text, emotion, note);
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
