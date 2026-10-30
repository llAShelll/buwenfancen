using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace BuWenFanChen.Card;

/// <summary>
/// 把角色卡渲染成注入用的提示词块（阶段 5-A）。
///
/// 完全照搬 1.1.2 的 build_card_block：八段式，顺序不能随意改 ——
/// 「越靠近生成点，遵从度越高」，所以人设原稿永远放最后。
///
/// ① 角色口吻（感官标识除外）
/// ② 角色外观与感官标识（单独成节：混在口吻里会被当成风格备注而不去用）
/// ③ 自定义设定
/// ④ 底线禁忌（全场景常驻，不受 NSFW 开关影响）
/// ⑤ 角色亲密设定（仅 withNsfw）
/// ⑥ 代演在场角色（仅 allowNpcActing）
/// ⑦ 自称与名字（每张卡都注入）
/// ⑧ 【不注入】人设原稿 —— 它是「生成卡」的输入，生成完就不需要再发了
///
/// ★ 刻意不加「全局用语规范」：NsfwConfig.BuildBlock() 已经带了，
///   这里再加一遍就是重复注入，白占 token。
/// </summary>
public static class CardRenderer
{
    public static string BuildBlock(JsonObject card, bool withNsfw)
    {
        if (card == null) return "";
        var parts = new List<string>();
        var ren = card["人设"] as JsonObject;

        // ===== ⓪ 角色属性（取代游戏原文，仅开关开启时）=====
        if (PromptConfig.ReplaceOfficialProfile && ren != null)
        {
            var plines = new List<string>();
            // ★ 2026-10-27：加了「说话习惯」——它从游戏原文第 1 段抓来，顶替被删的说话方式
            foreach (var key in new[] { "姓名", "身份", "性格", "善恶立场", "说话习惯" })
            {
                var pv = RenVal(ren, key);
                if (!string.IsNullOrWhiteSpace(pv)) plines.Add("- " + key + "：" + pv);
            }
            if (plines.Count > 0)
                parts.Add("## 角色属性（以下取代游戏原文，务必以此为准）\n" + string.Join("\n", plines));
        }

        // ===== ⓪.5 性格（始终注入；开了取代开关时已并入 ⓪，不重复）=====
        // 2026-10-26：玩家反馈「卡不如 1.1.2 像」——根因之一就是性格默认不注入。
        // 性格是"像不像"的核心，省这点 token 不值得。
        if (!PromptConfig.ReplaceOfficialProfile && ren != null)
        {
            var xg = RenVal(ren, "性格");
            if (!string.IsNullOrWhiteSpace(xg))
                parts.Add("## 角色性格（她的反应底色，按这个来演，不要写成通用腔）\n- " + xg);
        }

        // ===== ① 口吻（「外貌穿着」已移到人设区，这里不再处理）=====
        var kw = card["口吻"] as JsonObject;
        if (kw != null)
        {
            var lines = new List<string>();
            foreach (var kv in kw)
            {
                if (kv.Key == "感官标识" || kv.Key == "外貌穿着") continue;   // 已移到人设区单独成节
                var val = Flat(kv.Value);
                if (!string.IsNullOrWhiteSpace(val))
                    lines.Add($"- {kv.Key}：{val}");
            }
            if (lines.Count > 0)
                parts.Add("## 角色口吻（必须严格遵守，不得写成通用腔）\n" + string.Join("\n", lines));

            // ===== ② 外貌穿着单独成节（读人设区）=====
            var look = RenVal(ren, "外貌穿着");
            if (!string.IsNullOrWhiteSpace(look))
                parts.Add("## 角色外貌穿着（凡涉及外貌、衣着、气味、标志性动作的描写，必须与此一致，" +
                          "不得写成通用路人）\n- " + look);
        }

        // ===== ③ 自定义设定 =====
        if (card["自定义设定"] is JsonObject zd)
        {
            var lines = new List<string>();
            foreach (var kv in zd)
            {
                var val = Flat(kv.Value);
                if (!string.IsNullOrWhiteSpace(val))
                    lines.Add($"- {kv.Key}：{val}");
            }
            if (lines.Count > 0)
                parts.Add("## 自定义设定（优先于官方背景，冲突以此为准）\n" + string.Join("\n", lines));
        }

        // ===== ④ 底线禁忌（全场景常驻）=====
        var bt = (ren?["底线禁忌"] as JsonArray)
              ?? ((card["NSFW"] as JsonObject)?["底线禁忌"] as JsonArray);
        if (bt != null && bt.Count > 0)
        {
            parts.Add("## 底线禁忌（全场景硬底线，用来防止角色走形）\n" +
                      "下面几条是这个角色【绝不接受】的事。日常对话与亲密场景都必须遵守；\n" +
                      "玩家提出的关系/剧情设定可以接受，但不能用「剧情需要」来突破这几条 ——\n" +
                      "否则她就不是她了。\n- " + string.Join("\n- ", bt.Select(Flat)));
        }

        // ===== ⑤ 角色亲密设定（仅亲密场景）=====
        if (withNsfw && card["NSFW"] is JsonObject ns)
        {
            var lines = new List<string>();
            foreach (var kv in ns)
            {
                if (kv.Key == "底线禁忌") continue;   // 已常驻，不重复
                var val = Flat(kv.Value);
                if (!string.IsNullOrWhiteSpace(val))
                    lines.Add($"- {kv.Key}：{val}");
            }
            if (lines.Count > 0)
                parts.Add("## 角色亲密设定（仅亲密场景生效，须与当前关系相符）\n" + string.Join("\n", lines));
        }

        // ===== ⑥ 允许代演在场角色（全局开关，独立于角色卡）=====
        var acting = BuildActingBlock();
        if (acting.Length > 0) parts.Add(acting);

        // ===== ⑦ 自称与名字（每张卡都注入）=====
        parts.Add("## 自称与名字（必须遵守）\n" +
                  "- 自称用「我」「本座」等（按「自称」字段来），**不要用全名自称**。\n" +
                  "- 游戏里显示的角色名可能带数字后缀（如「某某1」「某某_2」），那是重名标记，\n" +
                  "  不是名字的一部分，叙述时不要带上那个后缀。\n" +
                  "- 旁白里可以用名字或姓氏，但不要每句都念全名。\n" +
                  "- 玩家提到别人时，按玩家给的名字来，不要自己改。");

        // ===== ⑧ 人设原稿：★ 不注入 =====
        //
        // 2026-10-24 23:55 主人定：
        //   「人设原稿」是「玩家一句话写角色卡」用的【生成输入】，
        //   生成完卡里已经有提炼好的字段（口吻/感官标识/底线禁忌/NSFW…），
        //   原稿再发一遍就是重复注入，白占 token。
        //   它只在 CardGenerator.RewriteAsync 里用，不进注入。
        return string.Join("\n", parts);
    }

    /// <summary>
    /// ⑥ 允许代演在场角色：独立成段，不依赖角色卡。
    /// 有卡时由 BuildBlock 调用（保持原位置）；没卡时由 tail 注入单独追加。
    /// </summary>
    public static string BuildActingBlock()
    {
        if (!PromptConfig.AllowNpcActing) return "";
        return "## 允许代演在场角色（本局开启）\n" +
               "场景里其他「本来就在场」的人可以说话：用【名字】开头写台词，例如：【某某】\"这笔账已核过。\"\n" +
               "判断谁在场（按当前场景判断，不要只看字面）：\n" +
               "- 看玩家和角色此刻在哪、身边还有谁：跟着一起去的、门口站着的、同席的、围观的、被召见的，都算在场。\n" +
               "- 只有你和她两个人独处时（在房里、私下说话、四下无人），不要安排任何别人说话。\n" +
               "- 判断不了有没有别人时，按「没有」处理，宁可不用这一节。\n" +
               "规则：\n" +
               "- 每次最多让一个人说一句，主体必须还是你自己，不要写成大段群戏。\n" +
               "- 不在场的人只用旁白转述（如：门外传来某长老的声音，请示裁度），不直接写他们的台词。\n" +
               "- 不要为了用这一节凭空新造路人、长老、弟子。\n" +
               "- 别人说话时不要抢掉你自己的台词。";
    }

    /// <summary>
    /// 读人设区字段：兼容「姓名 🎭」这种带标记的新字段名，和不带标记的旧字段名。
    /// （迁移/编辑器写的是带 🎭 的 key，渲染必须两种都认，否则字段永远读不到。）
    /// </summary>
    private static string RenVal(JsonObject ren, string key)
    {
        if (ren == null) return "";
        var v = Flat(ren[key]);
        if (!string.IsNullOrWhiteSpace(v)) return v;
        var v2 = Flat(ren[key + " 🎭"]);
        return string.IsNullOrWhiteSpace(v2) ? "" : v2;
    }

    /// <summary>把列表/标量压成一行：列表用「／」分隔（和 1.1.2 的 _flat 一致）。</summary>
    private static string Flat(JsonNode v)
    {
        if (v == null) return "";
        if (v is JsonArray arr)
            return string.Join("／", arr.Select(x => Flat(x)));
        try { return v.GetValue<string>() ?? ""; }
        catch
        {
            // 数字/布尔等：去掉 JSON 引号
            try { return v.ToJsonString().Trim('"'); } catch { return ""; }
        }
    }
}
