using System;
using System.Collections.Generic;

namespace BuWenFanChen;

/// <summary>
/// NPC 状态缓存（**只读**）。
///
/// 起因（2026-10-26 主人问）：游戏的好感度存在 NpcModel.Intimacy / MaxIntimacy，
/// 由游戏自己的 AddIntimacy 加，提示词里只有「按好感档演」。
/// 我们先做只读：每轮对话时从 NpcModel 读一次缓存起来，供控制台显示。
///
/// 注意：
///   · 只读属性（get_Intimacy / get_MaxIntimacy）+ 只读方法（GetIntimacyName），
///     不调用任何 setter / AddIntimacy —— 零风险。
///   · 读失败静默（绝不影响游戏）。
/// </summary>
public static class NpcStats
{
    public sealed class Stats
    {
        public int Intimacy;
        public int MaxIntimacy;
        public string Name = "";      // 档位名，如「相识」
        public long UpdatedAt;
    }

    private static readonly Dictionary<string, Stats> _cache = new();
    private static readonly object _lock = new();

    /// <summary>从 NpcModel 读一次（只读）。每轮对话调用一次即可。</summary>
    /// <param name="reportChange">true = 和上次缓存比，好感变了就写一行日志（用来观察游戏怎么涨的好感）</param>
    public static void Record(object npc, bool reportChange = false)
    {
        try
        {
            if (npc == null) return;
            var cfgId = BuWenFanChen.Memory.MemoryEngine.GetNpcCfgId(npc);
            if (string.IsNullOrEmpty(cfgId)) return;

            var st = new Stats();
            try
            {
                var p = npc.GetType().GetProperty("Intimacy");
                if (p != null) st.Intimacy = Convert.ToInt32(p.GetValue(npc) ?? 0);
            }
            catch { }
            try
            {
                var p = npc.GetType().GetProperty("MaxIntimacy");
                if (p != null) st.MaxIntimacy = Convert.ToInt32(p.GetValue(npc) ?? 0);
            }
            catch { }
            try
            {
                var m = npc.GetType().GetMethod("GetIntimacyName", Type.EmptyTypes);
                if (m != null) st.Name = m.Invoke(npc, null)?.ToString() ?? "";
            }
            catch { }
            st.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (_lock)
            {
                if (reportChange && _cache.TryGetValue(cfgId, out var prev) && prev.Intimacy != st.Intimacy)
                {
                    var delta = st.Intimacy - prev.Intimacy;
                    try
                    {
                        BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                            .LogInfo($"[好感] npc={cfgId} {prev.Intimacy} → {st.Intimacy}（{(delta > 0 ? "+" : "")}{delta}）"
                                   + $" 档位={st.Name}");
                    }
                    catch { }
                }
                _cache[cfgId] = st;
            }
        }
        catch { }
    }

    public static Stats Get(string cfgId)
    {
        try
        {
            lock (_lock) return (!string.IsNullOrEmpty(cfgId) && _cache.TryGetValue(cfgId, out var s)) ? s : null;
        }
        catch { return null; }
    }
}
