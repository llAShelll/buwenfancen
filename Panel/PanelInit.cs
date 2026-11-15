namespace BuWenFanChen.Panel;

using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using BuWenFanChen.Card;
using BuWenFanChen.Memory;
using BepInEx.Logging;

/// <summary>
/// ★ 2026-11-12：初值推断（1.4.0 P1）
/// 施工图：`施工_NPC数值面板_20261112.md` §5
///
/// 两条路：
///   · 首次自动 / `[重置本角色]` → 本地关键词规则（**零 LLM 成本**）
///   · `[按角色卡重新推断]`      → API 推断（点了才花，约 500 token；失败回退本地）
///
/// 铁律（§5.1）：**首次自动推断永远走本地规则** —— 不能让 NPC 一出现就偷偷花钱。
/// </summary>
internal static class PanelInit
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·面板初始化");

    // 默认初值（§1.2）
    internal const int BaseSubmission = 30;
    internal const int BaseShame = 60;
    internal const int BaseOpenness = 30;
    internal const int BaseCorruption = 0;

    /// <summary>
    /// 关键词规则表（§5.2）：命中任意一个词就按这条打分。
    /// 字段：关键词组 / 温度 / 态度 / 主动 / 话量 / 服从 / 羞耻 / 开放
    /// </summary>
    private static readonly (string[] Words, int W, int A, int I, int V, int Sub, int Sh, int Op)[] Rules =
    {
        (new[] { "温柔", "体贴", "心软", "善良", "包容", "温和", "柔和", "娴静", "温柔似水" }, 30, 0, 0, 0, 0, 0, 0),
        (new[] { "冷淡", "疏离", "寡言", "面无表情", "公事公办", "漠然", "清冷", "疏远" },  -30, 0, 0, -20, 0, 0, 0),
        (new[] { "傲娇", "嘴硬", "要强", "逞强", "不服软", "别扭", "口是心非" },             15, -25, 0, 0, 0, 0, 0),
        (new[] { "顺从", "听话", "乖巧", "依人", "温顺", "乖顺", "乖" },                     0, 30, 0, 0, 0, 0, 0),
        (new[] { "主动", "热络", "活泼", "元气", "爱闹", "黏人", "撒娇", "娇嗔" },           0, 0, 25, 25, 0, 0, 0),
        (new[] { "被动", "内向", "矜持", "安静", "沉静", "话少" },                           0, 0, -20, -20, 0, 0, 0),
        (new[] { "病娇", "占有", "偏执", "执念", "独占" },                                   0, -20, 40, 0, 0, 0, 0),
        (new[] { "三无", "无口", "沉默", "面瘫", "木讷", "不善言辞" },                      -20, 0, 0, -40, 0, 0, 0),
        (new[] { "服从", "调教", "主人", "侍奉", "听命", "恭顺" },                           0, 0, 0, 0, 25, 0, 0),
        (new[] { "羞", "矜持", "耻", "保守", "害臊", "青涩" },                               0, 0, 0, 0, 0, 20, 0),
        (new[] { "开放", "大胆", "放得开", "奔放", "不拘" },                                 0, 0, 0, 0, 0, 0, 25),
    };

    // ==================== 默认 ====================

    internal static PanelState DefaultState(string npcId)
    {
        var st = new PanelState { NpcId = npcId ?? "", InitSource = "default" };
        st.Base[0] = BaseSubmission;
        st.Base[1] = BaseShame;
        st.Base[2] = BaseOpenness;
        st.Base[3] = BaseCorruption;
        for (int i = 0; i < 4; i++) st.Values[i] = 0;
        for (int i = 0; i < 4; i++) st.Values[4 + i] = st.Base[i];
        return st;
    }

    // ==================== 本地关键词（零 LLM 成本） ====================

    /// <summary>
    /// 读角色卡 → 关键词打分 → 出该角色的初值（base + values 归位）。
    /// 没有卡 → 默认值（initSource=default）。不写盘，由 PanelStore 决定何时存。
    /// </summary>
    internal static PanelState InitByKeywords(string npcId)
    {
        var st = DefaultState(npcId);
        try
        {
            var card = CardStore.Load(npcId);
            if (card == null) return st;

            // 整卡文本参与匹配（人设 / 口吻 / 自定义设定 / NSFW 都会命中）
            var text = card.ToJsonString();

            int w = 0, a = 0, i = 0, v = 0, sub = 0, sh = 0, op = 0;
            var hits = new StringBuilder();
            foreach (var r in Rules)
            {
                var hit = r.Words.FirstOrDefault(x => text.Contains(x));
                if (hit == null) continue;
                w += r.W; a += r.A; i += r.I; v += r.V;
                sub += r.Sub; sh += r.Sh; op += r.Op;
                if (hits.Length > 0) hits.Append('/');
                hits.Append(hit);
            }

            st.Values[0] = Math.Clamp(w, -100, 100);
            st.Values[1] = Math.Clamp(a, -100, 100);
            st.Values[2] = Math.Clamp(i, -100, 100);
            st.Values[3] = Math.Clamp(v, -100, 100);

            st.Base[0] = Math.Clamp(BaseSubmission + sub, 0, 100);
            st.Base[1] = Math.Clamp(BaseShame + sh, 0, 100);
            st.Base[2] = Math.Clamp(BaseOpenness + op, 0, 100);
            st.Base[3] = BaseCorruption;
            for (int k = 0; k < 4; k++) st.Values[4 + k] = st.Base[k];

            st.InitSource = "keyword";
            Log.LogInfo($"[面板初始化] {npcId} 本地关键词推断：命中[{hits}] → 温度{w} 态度{a} 主动{i} 话量{v}"
                        + $" | 基 服从{st.Base[0]} 羞耻{st.Base[1]} 开放{st.Base[2]}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[面板初始化] 关键词推断失败（用默认值）：{e.GetType().Name}: {e.Message}");
        }
        return st;
    }

    // ==================== API 推断（点了才花） ====================

    /// <summary>
    /// `[按角色卡重新推断]`：让 LLM 读卡挑原型 + 给 NSFW 四项。
    /// temperature=0（同一张卡两次推断必须一致，否则 base 会跳）。
    /// 失败返回 Ok=false，调用方回退本地关键词规则。
    /// </summary>
    internal static async System.Threading.Tasks.Task<(bool Ok, PanelState State, string Msg)> AskLlmAsync(
        string npcId, LlmClient.Config cfg)
    {
        try
        {
            if (cfg == null || !cfg.IsValid)
                return (false, null, "拿不到 API 配置");

            var card = CardStore.Load(npcId);
            if (card == null)
                return (false, null, "这个角色还没有角色卡，先用本地规则");

            var names = string.Join("、", PanelArchetype.All.Select(p => p.Name));
            var sys = "你是角色设定分析器。只输出一个 JSON 对象，不要输出任何其它内容。";
            var user =
                "下面是这个角色卡的全部设定（JSON）：\n" + card.ToJsonString() + "\n\n" +
                "请判断她最接近下面哪个性格原型（只能选一个，必须原样写名字）：\n" + names + "\n\n" +
                "再给出 4 个 0~100 的整数（按卡里的 NSFW 设定判断）：\n" +
                "· submission 服从度：她多听玩家的话（主从关系）\n" +
                "· shame 羞耻心：她多难为情（反应强度，不是接受度）\n" +
                "· openness 开放度：她能接受多越界的玩法\n" +
                "· corruption 崩坏度：理智流失程度（刚认识一般是 0）\n\n" +
                "只输出这个 JSON：{\"archetype\":\"原型名\",\"submission\":0,\"shame\":0,\"openness\":0,\"corruption\":0,\"why\":\"40字内理由\"}";

            var (content, err) = await LlmClient.CompleteAsync(cfg, sys, user, 600, temperature: 0.0);
            if (string.IsNullOrEmpty(content))
                return (false, null, "API 调用失败：" + (err ?? "空响应"));

            // 容错：从返回里抠 JSON
            var json = content.Trim();
            var i0 = json.IndexOf('{');
            var i1 = json.LastIndexOf('}');
            if (i0 >= 0 && i1 > i0) json = json.Substring(i0, i1 - i0 + 1);

            if (JsonNode.Parse(json) is not JsonObject o)
                return (false, null, "AI 返回的不是 JSON");

            var st = DefaultState(npcId);
            var arch = o["archetype"]?.ToString() ?? "";
            var proto = PanelArchetype.Find(arch);
            if (proto != null)
            {
                st.Values[0] = proto.Warm;
                st.Values[1] = proto.Attitude;
                st.Values[2] = proto.Initiative;
                st.Values[3] = proto.Verbose;
            }
            // 四轴不让模型编数字：只认它挑的原型；NSFW 四项用模型给的整数
            st.Base[0] = Math.Clamp(ReadInt(o["submission"], BaseSubmission), 0, 100);
            st.Base[1] = Math.Clamp(ReadInt(o["shame"], BaseShame), 0, 100);
            st.Base[2] = Math.Clamp(ReadInt(o["openness"], BaseOpenness), 0, 100);
            st.Base[3] = Math.Clamp(ReadInt(o["corruption"], BaseCorruption), 0, 100);
            for (int k = 0; k < 4; k++) st.Values[4 + k] = st.Base[k];
            st.InitSource = "llm";

            bool archOk = proto != null;
            var msg = archOk
                ? $"AI 推断成功：{arch}（{o["why"]?.ToString() ?? ""}）"
                : $"AI 没给出有效原型（{arch}）→ 四轴按 0，NSFW 用 AI 数值";
            Log.LogInfo("[面板初始化] " + msg);
            return (true, st, msg);
        }
        catch (Exception e)
        {
            Log.LogWarning($"[面板初始化] AI 推断失败：{e.GetType().Name}: {e.Message}");
            return (false, null, "AI 推断失败：" + e.Message);
        }
    }

    private static int ReadInt(JsonNode v, int dflt)
    {
        if (v == null) return dflt;
        try
        {
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out var d)) return (int)Math.Round(d);
                if (jv.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var n)) return n;
            }
        }
        catch { }
        return dflt;
    }
}
