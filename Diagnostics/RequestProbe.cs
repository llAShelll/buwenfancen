using System;

namespace BuWenFanChen.Diagnostics;

/// <summary>
/// ★ 2026-11-11 主人要求：「上下文超限有没有什么探针可以加的」。
///
/// 目的：玩家再报「上下文超出模型上限，请在设置里调小上下文长度」时，
///   日志里**当场**就有答案 —— 这一轮请求到底多大、占玩家上下文的百分之几、是不是真的超了。
///   不用再像今晚这样翻 dump / 猜 / 抓包。
///
/// 做法：在 `CompleteBudgeted.Prefix` 里记下各段原始长度，
///   在 tail 构建完（= 最终长度）时打一行「[请求] 第N轮 估算 …」。
///   每轮一行，不刷屏（只在请求构建时打，不在每帧）。
///
/// 判读方法：
///   · 占用 &lt; 90%        → 长度没问题；再报超限就是别的原因（API 侧/空响应/模型上限）
///   · 占用 &gt; 100% 带 ★★ → 真的超了，看哪一段最大
///   · ctx 未知            → 首轮还没拿到 contextSize（FitTokens 在 CompleteBudgeted 之后跑）
/// </summary>
internal static class RequestProbe
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·请求");

    private static int _prefix = -1, _memory = -1, _context = -1;
    private static int _histCount = -1, _histChars = -1, _input = -1;
    private static int _round;

    /// <summary>Prefix 里调一次：记下这一轮各段的原始长度。</summary>
    internal static void Capture(string prefix, string memory, string context, object history, string input)
    {
        try
        {
            _prefix = prefix?.Length ?? -1;
            _memory = memory?.Length ?? -1;
            _context = context?.Length ?? -1;
            _input = input?.Length ?? -1;
            _histCount = History.CanonStore.CountItems(history);
            _histChars = History.CanonStore.EstimateChars(history);
        }
        catch { }
    }

    /// <summary>tail 构建完（这里是最终 tail）时调一次，打一行估算。</summary>
    internal static void Report(int tailLen)
    {
        try
        {
            _round++;
            long total = 0;
            if (_prefix > 0) total += _prefix;
            if (_memory > 0) total += _memory;
            if (_context > 0) total += _context;
            if (_input > 0) total += _input;
            if (tailLen > 0) total += tailLen;
            if (_histChars > 0) total += _histChars;
            else if (_histCount > 0) total += _histCount * 300L;   // 读不到字符数时按 300 字/条估

            int ctx = 0, maxTok = -1;
            float cpt = 1.5f;
            ChatBudgetProbe.Snapshot(out ctx, out maxTok, out cpt);

            int estTok = cpt > 0.1f ? (int)(total / cpt) : -1;
            int totalTok = (estTok > 0 && maxTok > 0) ? estTok + maxTok : estTok;
            int pct = (ctx > 0 && totalTok > 0) ? (int)(totalTok * 100L / ctx) : -1;
            bool danger = totalTok > 0 && ctx > 0 && totalTok > ctx;

            var msg = new System.Text.StringBuilder("[请求] 第").Append(_round).Append("轮 估算 ")
                .Append(total.ToString("N0")).Append(" 字")
                .Append("（prefix ").Append(_prefix)
                .Append(" + memory ").Append(_memory)
                .Append(" + context ").Append(_context)
                .Append(" + hist ").Append(_histCount).Append("条/").Append(_histChars)
                .Append(" + tail ").Append(tailLen)
                .Append(" + input ").Append(_input).Append("）")
                .Append(" ≈ ").Append(estTok.ToString("N0")).Append(" tok");
            if (maxTok > 0) msg.Append(" + max_tokens ").Append(maxTok.ToString("N0"))
                               .Append(" = ").Append(totalTok.ToString("N0")).Append(" tok");
            msg.Append(ctx > 0 ? " / 上下文 " + ctx.ToString("N0") + "（" + pct + "%）" : " / 上下文未知（首轮）");
            if (danger) msg.Append(" ★★ 预计超限！");

            Log.LogInfo(msg.ToString());
        }
        catch { }
    }
}
