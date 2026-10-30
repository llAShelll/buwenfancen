using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Card;

/// <summary>
/// 角色卡可执行性检查（阶段 5-D）。
///
/// 两步走，照搬 1.1.2：
///   ① 规则粗筛：空字段 / 含糊词 / 只有形容词没有动作 → 直接标 vague（不花 token）
///   ② 模型判定：只把规则判不了的项交给模型，要求给出「改成可观察行为」的建议
///
/// 为什么需要：角色卡里写「适当撒娇」这种话，模型读到也不知道具体怎么演，
/// 等于白写。检查的作用是把这类含糊项挑出来，逼着改成能落地的东西。
/// </summary>
public static class CardChecker
{
    /// <summary>含糊词表（和 1.1.2 一致）</summary>
    private static readonly string[] VagueWords =
    {
        "适当", "稍微", "略微", "一点", "一点点", "尽量", "尽可能",
        "有时候", "有时", "偶尔", "可能", "也许", "大概", "大致", "差不多",
        "比较好", "好些", "适度", "若干", "某个", "之类", "等等", "什么的",
        "看情况", "视情况", "自然", "随性", "灵活", "酌情", "合理地", "充分地",
    };

    /// <summary>这些字段不参与「读不懂」检查（结构化数值/数组本来就好懂）</summary>
    private static readonly HashSet<string> CheckSkip = new()
    {
        "启用", "_版本", "_来源", "_自动生成基准", "AI生卡", "人设原稿", "_aiBaseline",
    };

    public sealed class Item
    {
        public string Section { get; set; } = "";
        public string Field { get; set; } = "";
        public string Value { get; set; } = "";
        public string Verdict { get; set; } = "ok";     // ok / vague / empty
        public string Reason { get; set; } = "";
        public string Suggestion { get; set; } = "";    // 模型给的修改建议

        public string FullName => Section + "·" + Field;
    }

    // ==================== ① 规则粗筛 ====================

    public static List<Item> RuleScan(JsonObject card)
    {
        var result = new List<Item>();
        if (card == null) return result;

        foreach (var secName in new[] { "人设", "口吻", "自定义设定", "NSFW" })
        {
            if (card[secName] is not JsonObject sec) continue;
            foreach (var kv in sec)
            {
                if (CheckSkip.Contains(kv.Key)) continue;
                var txt = Flat(kv.Value).Trim();

                if (string.IsNullOrEmpty(txt))
                {
                    // ★ 没填的字段也要报出来（"没填"不等于"没问题"）
                    result.Add(new Item
                    {
                        Section = secName, Field = kv.Key, Value = "",
                        Verdict = "empty", Reason = "这项还没填",
                    });
                    continue;
                }

                if (kv.Key == "底线禁忌")
                {
                    result.Add(new Item { Section = secName, Field = kv.Key, Value = txt, Verdict = "ok" });
                    continue;
                }

                var hits = VagueWords.Where(w => txt.Contains(w)).Distinct().ToList();
                if (hits.Count > 0)
                {
                    result.Add(new Item
                    {
                        Section = secName, Field = kv.Key, Value = txt,
                        Verdict = "vague",
                        Reason = "含含糊词：" + string.Join("、", hits) + "（模型不知道具体到什么程度）",
                    });
                    continue;
                }

                // 太短且没有任何可执行信息（没数字、没「当」）
                if (txt.Length <= 4 && !txt.Any(char.IsDigit) && !txt.Contains('当'))
                {
                    result.Add(new Item
                    {
                        Section = secName, Field = kv.Key, Value = txt,
                        Verdict = "vague", Reason = "只有形容词，没写具体怎么做",
                    });
                    continue;
                }

                result.Add(new Item { Section = secName, Field = kv.Key, Value = txt, Verdict = "ok" });
            }
        }
        return result;
    }

    // ==================== ② 模型判定 ====================

    private const string ModelCheckSystem = @"你是角色卡可执行性检查员。下面这些是玩家写的角色卡字段。
对每一条判断：「一个角色扮演模型读到它，能不能明确知道该怎么表现？」
· 能 → 不要出现在输出里
· 不能 → 输出 {字段, 原因, 建议}

要求：
- 原因要具体指出哪个词/哪个地方读不懂
- 建议要改成【可观察的行为】，不要用形容词换形容词
  例如「适当撒娇」→「每 3~4 轮可以说一次'你别走'，语气放软，不拖长音」
- 不许改变原意，也不许加玩家没说的内容
- 只输出严格 JSON 数组，不要 markdown：
[{""字段"":""口吻·语气基调"",""原因"":""…"",""建议"":""…""}]";

    /// <summary>只把规则判不了（这里指含糊但非空）的项交给模型</summary>
    private static async Task<Dictionary<string, (string Reason, string Suggestion)>> ModelCheckAsync(
        List<Item> vagueItems)
    {
        var result = new Dictionary<string, (string, string)>();
        if (vagueItems.Count == 0) return result;

        var cfg = Memory.MemoryEngine.GetLlmConfig(null);
        // ★ 角色卡专用模型（config.json 的 cardModel，空则沿用游戏模型）
        if (cfg != null && !string.IsNullOrWhiteSpace(PromptConfig.CardModel))
            cfg.Model = PromptConfig.CardModel;
        if (!cfg.IsValid) return result;

        var sb = new StringBuilder();
        foreach (var it in vagueItems)
            sb.AppendLine($"- {it.FullName}：{it.Value}");

        var (content, _) = await Memory.LlmClient.CompleteAsync(cfg, ModelCheckSystem, sb.ToString(), 2500);
        if (content == null) return result;

        try
        {
            var a = content.IndexOf('[');
            var z = content.LastIndexOf(']');
            if (a < 0 || z <= a) return result;
            var arr = JsonNode.Parse(content.Substring(a, z - a + 1)) as JsonArray;
            if (arr == null) return result;
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var field = o["字段"]?.GetValue<string>();
                if (string.IsNullOrEmpty(field)) continue;
                result[field] = (
                    o["原因"]?.GetValue<string>() ?? "",
                    o["建议"]?.GetValue<string>() ?? ""
                );
            }
        }
        catch { }
        return result;
    }

    /// <summary>完整检查：规则粗筛 + 模型判定，返回合并后的清单</summary>
    public static async Task<(bool Ok, List<Item> Items, string Msg)> CheckAsync(string npcCfgId, bool useModel = true)
    {
        var card = CardStore.Load(npcCfgId);
        if (card == null) return (false, null, "这个角色还没有角色卡");

        var items = RuleScan(card);

        // 只有「含糊」（非空但读不懂）的才值得交给模型；空的直接报
        // ★ 2026-10-25 主人要：检查强度可选「只跑规则（免费快）」还是「规则+模型（更准）」
        var vague = useModel ? items.Where(i => i.Verdict == "vague").ToList() : new List<Item>();
        if (vague.Count > 0)
        {
            var modelResult = await ModelCheckAsync(vague);
            foreach (var it in vague)
            {
                if (modelResult.TryGetValue(it.FullName, out var r))
                {
                    it.Reason = string.IsNullOrEmpty(r.Reason) ? it.Reason : r.Reason;
                    it.Suggestion = r.Suggestion;
                }
            }
        }

        var bad = items.Count(i => i.Verdict != "ok");
        var msg = bad == 0
            ? $"检查通过（{items.Count} 项全部可执行）"
            : $"发现 {bad} 项需要改（空 {items.Count(i => i.Verdict == "empty")} / 含糊 {items.Count(i => i.Verdict == "vague")}）";
        return (true, items, msg);
    }

    // ==================== 工具 ====================

    /// <summary>应用玩家勾选的检查建议（adopted: [{区块,字段,新值}]）。旧卡自动备份。</summary>
    public static (bool Ok, string Msg) ApplySuggestions(string npcCfgId, JsonArray adopted, bool backup = true)
    {
        var card = CardStore.Load(npcCfgId);
        if (card == null) return (false, "这个角色还没有角色卡");
        try
        {
            var applied = 0;
            foreach (var node in adopted)
            {
                if (node is not JsonObject o) continue;
                var sec = o["区块"]?.GetValue<string>() ?? "";
                var fld = o["字段"]?.GetValue<string>() ?? "";
                var val = o["新值"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(sec) || string.IsNullOrEmpty(fld)) continue;
                var curSec = card[sec] as JsonObject;
                if (curSec == null) { curSec = new JsonObject(); card[sec] = curSec; }
                curSec[fld] = val;
                applied++;
            }

            string backupPath = null;
            if (backup)
            {
                try
                {
                    var p0 = CardStore.PathOf(npcCfgId);
                    if (System.IO.File.Exists(p0))
                    {
                        backupPath = p0 + ".检查前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        System.IO.File.Copy(p0, backupPath, true);
                    }
                }
                catch { }
            }

            var saved = CardStore.Save(npcCfgId, card);

            // ★ 2026-10-25 主人要：优化前备份 → 确认应用成功后自动删掉备份
            if (saved && backup && !string.IsNullOrEmpty(backupPath))
            {
                try { System.IO.File.Delete(backupPath); } catch { }
            }

            return saved
                ? (true, "已应用 " + applied + " 项" + (backup ? "（备份已自动清理）" : ""))
                : (false, "保存失败");
        }
        catch (Exception e)
        {
            return (false, "应用失败：" + e.Message);
        }
    }

    private static string Flat(JsonNode v)
    {
        if (v == null) return "";
        if (v is JsonArray arr)
            return string.Join("／", arr.Select(x => Flat(x)));
        try { return v.GetValue<string>() ?? ""; }
        catch
        {
            try { return v.ToJsonString().Trim('"'); } catch { return ""; }
        }
    }
}
