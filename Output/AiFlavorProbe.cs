namespace BuWenFanChen.Output;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// ★ 2026-11-13 主人要求：AI 腔 / 鱼八股密度探针。
///
/// 目的：把主人列出的「鱼八股」句式（元评价腔 / 表态报账腔 / 网文留白腔）做成**程序统计**，
/// 每轮输出打一行日志。**只统计，绝不改 content**——改了会让语音 MOD 的显示内容一致性检查失败（TTS 哑）。
///
/// 依据：`AI腔治理_跨项目方法论_20261107.md` §替换二（密度预算：密度统计必须程序做，不能靠模型自己数）
/// 与 §验收（看行为指标，不看词表）。
///
/// 词单来自主人 2026-11-13 的实测清单（约 40 个）。
/// </summary>
internal static class AiFlavorProbe
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·AI腔");

    /// <summary>(标签, 正则) —— 全部不区分大小写（中文无所谓），统计命中次数</summary>
    private static readonly (string Name, Regex Re)[] Patterns = BuildPatterns();

    private static (string, Regex)[] BuildPatterns()
    {
        var list = new List<(string, Regex)>();
        void Add(string name, string pattern)
        {
            try { list.Add((name, new Regex(pattern, RegexOptions.Compiled))); } catch { }
        }

        // ===== ① 表态报账腔（我认 / 我记下了 / 这账） =====
        Add("报账", @"(我记下了|我记得了|我记住了|这个账|这账我|这话我记下|这话我收起|这话我留着|你这句话我记着|行，?我认|我认(?!识|路|真)|认下来|认得实|就是认了|我收下了)");
        // ===== ② 元评价腔（这句话稳 / 比刚才轻 / 接得漂亮） =====
        Add("元评价", @"(这句话(稳|重|轻|平|直|强|好|不错|接得)|这话(接得|我不接|是ta|是Ta|告诉)|接得漂亮|什么心思都写着|没接你)");
        Add("比较", @"比(之前|刚才|谁|那|这|你)[^。，！？\n]{0,6}(稳|重|轻|平|直|沉|强|响)");
        Add("单字评价", @"(很平|很直|很稳|这话稳|声音没抖)");
        // ===== ③ 网文留白腔（短句指令） =====
        Add("留白腔", @"(睡吧，?我守着|你去，?我跟|回去睡|不拦你|我不拦|你别催|我不催|你掂量|你自己掂量|你收不回去|这话我不接)");
        // ===== ④ 泛化留白（只在句首/句尾紧邻时统计，减少误伤） =====
        Add("泛留白", @"(^|[\n。“” ])(稳|平|直)([。，！？\n]|$)");

        return list.ToArray();
    }

    private static int _lastLogTurn = -1;

    /// <summary>统计一段输出里的鱼八股密度，打日志（每轮一次）。</summary>
    public static void Report(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            var body = StripParensAndQuotesLite(text);
            int chars = body.Length;
            if (chars < 10) return;

            var hits = new List<string>();
            int total = 0;
            foreach (var (name, re) in Patterns)
            {
                int c;
                try { c = re.Matches(body).Count; } catch { c = 0; }
                if (c > 0) { hits.Add(name + "×" + c); total += c; }
            }
            if (total == 0) return;

            double per100 = Math.Round(total * 100.0 / chars, 1);
            Log.LogInfo($"[AI腔] 字数 {chars} 命中 {total} 处（{per100}/百字）明细：{string.Join("、", hits)}");
        }
        catch { }
    }

    /// <summary>轻量清洗：去掉括号旁白与引号之外的符号，保留正文（用于密度分母）。</summary>
    private static string StripParensAndQuotesLite(string s)
    {
        // 只把换行/空白算掉；括号和引号保留（鱼八股常出现在旁白/台词里）
        return s.Replace("\r", "").Replace("\n", "").Trim();
    }
}
