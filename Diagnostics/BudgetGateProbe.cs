using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BuWenFanChen.Diagnostics;

/// <summary>
/// ★ 2026-11-12（PRO 审核后新增，主人要求「下次发包时带上」）：把探针装到**真正的闸门**上。
///
/// 为什么必须加（PRO 审核 §四.1 的批评，说得对）：
///   前三次修复（max_tokens 只读 / canon 对齐 / maxChars 动态）全部改在**闸门上游**，
///   而玩家一直撞的那道闸门 —— 游戏自己在发请求前的预算预检 —— **自始至终一次都没被观测过**。
///   我们一直在调「多大的球会撞门框」，从没量过门框。
///
/// 闸门位置（il2cpp metadata + ilspycmd 反编译实证）：
///   JNGame.Ainpc.Llm.BudgetedLlmCall
///     · Prepare(bool retry = false) → ChatBudget.Plan                    ← 优先（同步）
///     · ExecuteAsync(ILlmProvider, CancellationToken, Action&lt;...&gt;, bool)  ← 兜底
///   位于 LogRequest 之后、HTTP 之前。LogOutput(7) 里「请求体已落盘 → 随后 Success=False/HttpCode=0」
///   就是它拦的。
///
/// 一次失败日志即可同时裁决四个假设：
///   A. Plan.History 写回是否账实不符   → 看 Plan.DroppedHistoryCount / TotalChars
///   B. 游戏更新是否收紧判定             → 看闸门手里的预算常量 vs 请求规模
///   C. charsPerToken 校准漂移           → 看 m_CharsPerToken 与最终估算
///   E. Harmony 改的参数有没有传下来     → ★ 看闸门实例里的 maxChars 是 60000 还是 24000
///
/// 纯读 + try/catch，零行为改动、不碰任何参数。
/// </summary>
[HarmonyPatch]
internal static class BudgetGateProbe
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·闸门");

    private static int _count;
    private static bool _fieldsDumped;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        try
        {
            var t = AccessTools.TypeByName("JNGame.Ainpc.Llm.BudgetedLlmCall");
            if (t == null)
            {
                Log.LogWarning("[闸门] 找不到 JNGame.Ainpc.Llm.BudgetedLlmCall → 闸门探针不可用");
                return null;
            }

            MethodBase best = null;
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null || m.Name != "Prepare") continue;
                best = m;
                break;
            }
            if (best == null) best = AccessTools.Method(t, "ExecuteAsync");

            if (best == null)
            {
                Log.LogWarning("[闸门] 找不到 Prepare / ExecuteAsync → 闸门探针不可用");
                return null;
            }

            var sb = new StringBuilder("[闸门] 命中 ");
            sb.Append(best.DeclaringType?.FullName).Append('.').Append(best.Name).Append('(');
            var ps = best.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
            }
            sb.Append(')');
            Log.LogInfo(sb.ToString());
            return best;
        }
        catch (Exception e)
        {
            Log.LogWarning("[闸门] TargetMethod 失败：" + e.GetType().Name + " " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// ★ 2026-11-12（本地探针实证后的**根因修复**）：
    ///
    /// 实证数据（本机两个场景各一轮）：
    ///   _Prefix 6529/7059 + _Context 699/928 + _Tail 8748/6174 + _Input 6/23 = fixed 15,982 / 14,184
    ///   m_MaxChars **恒为 24,000** ← 换场景、换 NPC 都不变，确认是游戏写死常量
    ///   → fixed 越大，留给历史的越少；玩家 A 的 fixed ≈ 20,743 → 只剩 3,257 字
    ///     → 连 m_Reserve=4 轮的保底都凑不出 → CanSend=False → required_context_exceeds_budget
    ///
    /// 为什么我们的 FitChars 改 60,000 没用：**Harmony 改的是 FitChars 的参数（盒装值），
    ///   而 BudgetedLlmCall 拿的是调用方另一个局部变量** —— 那道闸门手里一直是 24,000。
    ///
    /// 修法：在 Prepare 真正使用之前，把 m_MaxChars 改成我们自己算的值。
    ///   公式与 ChatBudgetProbe 一致：(contextSize - outputTokens) × charsPerToken × 0.85，clamp 4000~60000。
    ///
    /// 安全性：
    ///   · m_MaxChars 只是**本地裁历史的字符预算**，不是发给 API 的 max_tokens（那是 m_OutputTokens，绝不碰）
    ///   · 只在「算出来比游戏原值大」时才改 —— 永远不低于游戏原值、永远不超过玩家上下文的 85%
    /// </summary>
    [HarmonyPrefix]
    internal static void Prefix(object __instance)
    {
        if (__instance == null) return;
        try
        {
            int ctx = GetInt(__instance, "m_ContextSize");
            int outTok = GetInt(__instance, "m_OutputTokens");
            float cpt = GetFloat(__instance, "m_CharsPerToken");
            int oldMax = GetInt(__instance, "m_MaxChars");

            if (ctx <= 0 || outTok <= 0 || cpt <= 0.1f || oldMax <= 0) return;

            long usable = (long)((ctx - outTok) * (double)cpt * 0.85);
            if (usable > MaxCharsCeil) usable = MaxCharsCeil;
            if (usable < 4000) usable = 4000;
            int newMax = (int)usable;

            if (newMax <= oldMax) return;   // 不放宽就不动（保持游戏原值）

            bool wrote = false;
            var fl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = __instance.GetType();

            var p = t.GetProperty("m_MaxChars", fl);
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(__instance, newMax); wrote = true; } catch { }
            }
            if (!wrote)
            {
                var f = t.GetField("m_MaxChars", fl);
                if (f != null)
                {
                    try { f.SetValue(__instance, newMax); wrote = true; } catch { }
                }
            }

            int readback = GetInt(__instance, "m_MaxChars");
            _gateFixCount++;
            bool logNow = _gateFixCount <= 3 || _gateFixCount % 20 == 0;
            if (logNow)
            {
                Log.LogInfo($"[闸门] ★ 预算修正 m_MaxChars {oldMax} → {newMax}（回读={readback}）"
                            + $" | ctx={ctx} outTok={outTok} cpt={cpt:F2}"
                            + (wrote ? "" : "   ⚠ 写入失败（字段只读？）")
                            + " —— 只改本地裁剪预算，不动发给 API 的 max_tokens");
            }
        }
        catch (Exception e)
        {
            try { Log.LogWarning("[闸门] Prefix 修正失败：" + e.GetType().Name + " " + e.Message); } catch { }
        }
    }

    /// <summary>与 ChatBudgetProbe 保持一致的字符预算上限</summary>
    private const int MaxCharsCeil = 60000;

    private static int _gateFixCount;

    private static int GetInt(object o, string name)
    {
        try
        {
            var fl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = o.GetType();
            var p = t.GetProperty(name, fl);
            if (p != null) { var v = p.GetValue(o); return v == null ? -1 : Convert.ToInt32(v); }
            var f = t.GetField(name, fl);
            if (f != null) { var v = f.GetValue(o); return v == null ? -1 : Convert.ToInt32(v); }
            return -1;
        }
        catch { return -1; }
    }

    private static float GetFloat(object o, string name)
    {
        try
        {
            var fl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = o.GetType();
            var p = t.GetProperty(name, fl);
            if (p != null) { var v = p.GetValue(o); return v == null ? -1f : Convert.ToSingle(v); }
            var f = t.GetField(name, fl);
            if (f != null) { var v = f.GetValue(o); return v == null ? -1f : Convert.ToSingle(v); }
            return -1f;
        }
        catch { return -1f; }
    }

    [HarmonyPostfix]
    internal static void Postfix(object __instance, MethodBase __originalMethod, ref object __result)
    {
        try
        {
            if (__instance == null) return;
            _count++;

            // 第一次先把实例的**全部字段**列出来 —— 游戏私有字段名我们并不知道，
            // 先看清单，后面才知道该盯哪几个（PRO 给的 m_MaxChars 等只是推测名）。
            if (!_fieldsDumped)
            {
                _fieldsDumped = true;
                Log.LogInfo("[闸门] 实例字段清单（一次性，后面只打数值）：" + DescribeAllFields(__instance));
            }

            var line = new StringBuilder();
            line.Append("[闸门] 第").Append(_count).Append("次 ")
                .Append(__originalMethod?.Name ?? "?")
                .Append(" → ").Append(DescribeScalarFields(__instance));

            bool canSendFalse = false;
            if (__result != null)
            {
                var rt = __result.GetType();
                if (rt.GetProperty("CanSend") != null || rt.GetProperty("EstimatedTokens") != null)
                {
                    var canSend = P(__result, "CanSend");
                    canSendFalse = canSend == "False";
                    line.Append(" | Plan: CanSend=").Append(canSend)
                        .Append(" EstTok=").Append(P(__result, "EstimatedTokens"))
                        .Append(" TotalChars=").Append(P(__result, "TotalChars"))
                        .Append(" FixedChars=").Append(P(__result, "FixedChars"))
                        .Append(" Dropped=").Append(P(__result, "DroppedHistoryCount"))
                        .Append(" TrimReason=").Append(P(__result, "TrimReason"));
                }
                else
                {
                    line.Append(" | 返回类型=").Append(rt.Name);
                }
            }

            if (canSendFalse)
                Log.LogWarning(line.ToString() + "   ★★ CanSend=False → 游戏即将抛 ContextBudgetExceeded");
            else
                Log.LogInfo(line.ToString());
        }
        catch (Exception e)
        {
            try { Log.LogWarning("[闸门] 读取失败：" + e.GetType().Name + " " + e.Message); } catch { }
        }
    }

    // ───────── 反射读取（读不到给 "?"，绝不抛）─────────

    private static string P(object obj, string name)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return SafeStr(p.GetValue(obj));
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return SafeStr(f.GetValue(obj));
            return "?";
        }
        catch { return "?"; }
    }

    private static string SafeStr(object v)
    {
        if (v == null) return "null";
        try
        {
            if (v is string s)
            {
                s = s.Replace("\r", "\\r").Replace("\n", "\\n");
                return "\"" + (s.Length > 40 ? s.Substring(0, 40) + "…" : s) + "\"";
            }
            return v.ToString();
        }
        catch { return "?"; }
    }

    /// <summary>一次性：列出所有字段/属性（名 + 类型 + 值或类型名）</summary>
    private static string DescribeAllFields(object inst)
    {
        var sb = new StringBuilder();
        try
        {
            var t = inst.GetType();
            var fl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var f in t.GetFields(fl))
            {
                string v;
                try
                {
                    var val = f.GetValue(inst);
                    if (val == null) v = "null";
                    else if (val is string s) v = "str(" + s.Length + ")";
                    else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) v = val.ToString();
                    else v = val.GetType().Name;
                }
                catch (Exception e) { v = "<" + e.GetType().Name + ">"; }
                sb.Append(f.Name).Append('(').Append(f.FieldType.Name).Append(")=").Append(v).Append(" | ");
            }

            foreach (var p in t.GetProperties(fl))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                string v;
                try
                {
                    var val = p.GetValue(inst);
                    if (val == null) v = "null";
                    else if (val is string s) v = "str(" + s.Length + ")";
                    else if (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum) v = val.ToString();
                    else v = val.GetType().Name;
                }
                catch (Exception e) { v = "<" + e.GetType().Name + ">"; }
                sb.Append(p.Name).Append('[').Append(p.PropertyType.Name).Append("]=").Append(v).Append(" | ");
            }
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>每轮：只打标量字段（数值/字符串/枚举），Il2Cpp 对象只留类型名，避免刷屏</summary>
    private static string DescribeScalarFields(object inst)
    {
        var sb = new StringBuilder();
        try
        {
            var t = inst.GetType();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = f.FieldType;
                var scalar = ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(decimal);
                if (!scalar) continue;

                string v;
                try
                {
                    var val = f.GetValue(inst);
                    if (val is string s) v = s.Length > 30 ? "str(" + s.Length + ")" : "\"" + s + "\"";
                    else v = val?.ToString() ?? "null";
                }
                catch { v = "?"; }
                sb.Append(f.Name).Append('=').Append(v).Append(' ');
            }
        }
        catch { }
        return sb.ToString().Trim();
    }
}
