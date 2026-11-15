namespace BuWenFanChen.Panel;

using System;

/// <summary>
/// ★ 2026-11-12：16 个性格原型 + 匹配度 + 模板套用（1.4.0 P1）
/// 施工图：`施工_NPC数值面板_20261112.md` §1.4 / §6.2
///
/// 数据来源：4 轴组合 → 市面常见 RP 性格。**只读派生，不单独存储**。
/// 匹配度算法（§1.4）：diff = 四轴差值之和；match = 100 - diff*100/(4*200)；match &lt; 45 显示「未定型」。
/// 模板套用只改四轴 values（四轴 base 恒为 0），不碰 NSFW 的 base。
/// </summary>
internal static class PanelArchetype
{
    internal sealed class Proto
    {
        public string Name;
        public int Warm, Attitude, Initiative, Verbose;
        internal Proto(string n, int w, int a, int i, int v) { Name = n; Warm = w; Attitude = a; Initiative = i; Verbose = v; }
    }

    /// <summary>16 原型（施工图 §1.4 原表）</summary>
    internal static readonly Proto[] All =
    {
        new("傲娇",   60, -50,  40,  50),
        new("病娇",   70, -60,  90,  60),
        new("三无",  -60,   0, -50, -90),
        new("天然呆", 50,  30,   0,  40),
        new("腹黑",    0, -30,  20,  50),
        new("冷艳",  -70, -20,  10, -40),
        new("元气",   70,  10,  70,  80),
        new("大姐姐", 50,  40,  30,  50),
        new("弱气",   20,  70, -40, -20),
        new("强气",  -20, -70,  50,  30),
        new("忧郁",  -40,  10, -30, -60),
        new("忠犬",   80,  80,  40,  40),
        new("女王",  -30, -80,  70,  40),
        new("小恶魔", 30, -40,  60,  60),
        new("圣女",   90,  60, -20,  10),
        new("疯批",  -50, -70,  80,  10),
    };

    /// <summary>匹配度 &lt; 45 时显示「未定型」</summary>
    internal const int UnformedThreshold = 45;

    /// <summary>按 4 轴找最像的原型（返回原型名 + 匹配度；匹配度不足时名字给「未定型」）</summary>
    internal static (string Name, int Match) Best(int[] values)
    {
        if (values == null || values.Length < 4) return ("未定型", 0);
        try
        {
            // ★ 2026-11-12 离线实测补丁：四轴全在死区时**不硬套原型**。
            //   否则全 0 会被算出「天然呆 85% / 腹黑 88%」这种虚高匹配（4*200 分母的副作用）。
            int dz = Math.Max(0, PromptConfig.PanelDeadZone);
            bool allDead = true;
            for (int i = 0; i < 4; i++) if (Math.Abs(values[i]) > dz) { allDead = false; break; }
            if (allDead) return ("未定型", 0);

            int bestMatch = 0;
            string bestName = "未定型";
            foreach (var p in All)
            {
                int diff = Math.Abs(values[0] - p.Warm) + Math.Abs(values[1] - p.Attitude)
                         + Math.Abs(values[2] - p.Initiative) + Math.Abs(values[3] - p.Verbose);
                int match = 100 - diff * 100 / (4 * 200);
                if (match > bestMatch) { bestMatch = match; bestName = p.Name; }
            }
            if (bestMatch < UnformedThreshold) return ("未定型", bestMatch);
            return (bestName, bestMatch);
        }
        catch { return ("未定型", 0); }
    }

    internal static Proto Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in All) if (p.Name == name) return p;
        return null;
    }

    /// <summary>
    /// 模板套用：把四轴设成该原型的标准值（只改 values，不碰 NSFW 的 base）。
    /// 返回是否成功。
    /// </summary>
    internal static bool TryApply(PanelState st, string name)
    {
        var p = Find(name);
        if (p == null || st == null) return false;
        st.Values[0] = p.Warm;
        st.Values[1] = p.Attitude;
        st.Values[2] = p.Initiative;
        st.Values[3] = p.Verbose;
        st.InitSource = "template";
        return true;
    }
}
