// 最小探针 B：验证 LLM 调用层能不能安全 hook + 用 .NET 反射读 Il2Cpp 的 messages
//
// 目的：方案 B（hook Complete 重排 messages）的前置可行性验证。
// 背景：2026-10-24 试过完整探针，0xc0000005 崩溃，当时判断「Il2Cpp 对象不能这样碰」。
// 本探针分【4 层】探测，每层单独打日志，崩了能定位是哪一层：
//   ① 只打日志（不碰 messages 内容）
//   ② 反射读 Count
//   ③ 反射读 [0] 的类型
//   ④ 读 [0].Role / [0].Content
// 只跑前 3 次调用，避免刷屏。
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen
{
    [HarmonyPatch]
    internal static class LlmProbeMin
    {
        private static int _hits;
        private static bool _disabled;

        static MethodBase TargetMethod()
        {
            try
            {
                var t = AccessTools.TypeByName("JNGame.Ainpc.Llm.OpenAiCompatProvider");
                if (t == null) { Log("[探针B] 没找到 OpenAiCompatProvider"); return null; }
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Complete") continue;
                    var sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                    if (!sig.Contains("IReadOnlyList") || !sig.Contains("LlmCallOptions")) continue;
                    Log($"[探针B] 命中 {t.FullName}.Complete 参数({sig})");
                    return m;
                }
                Log("[探针B] 没找到 Complete(IReadOnlyList, LlmCallOptions)");
            }
            catch (Exception e) { Log($"[探针B] TargetMethod 异常 {e.GetType().Name}: {e.Message}"); }
            return null;
        }

        [HarmonyPrefix]
        static void Prefix(object __0)
        {
            if (_disabled || _hits >= 3) return;
            _hits++;
            try
            {
                // ① 只打日志，不碰内容
                Log($"[探针B #{_hits}] ① hook 命中，参数类型={__0?.GetType().FullName ?? "null"}");
                if (__0 == null) return;

                // ② 反射读 Count
                int cnt = -1;
                try
                {
                    var p = __0.GetType().GetProperty("Count");
                    cnt = p != null ? Convert.ToInt32(p.GetValue(__0)) : -2;
                    Log($"[探针B #{_hits}] ② Count = {cnt}");
                }
                catch (Exception e)
                {
                    Log($"[探针B #{_hits}] ② Count 异常 {e.GetType().Name}: {e.Message}");
                    return;
                }

                // ③ 反射读第 0 条
                if (cnt > 0)
                {
                    try
                    {
                        var idx = __0.GetType().GetProperty("Item");
                        var first = idx?.GetValue(__0, new object[] { 0 });
                        Log($"[探针B #{_hits}] ③ [0] 类型 = {first?.GetType().FullName ?? "null"}");
                        if (first != null)
                        {
                            var rp = first.GetType().GetProperty("Role");
                            Log($"[探针B #{_hits}] ④ Role = {rp?.GetValue(first)}");
                            var cp = first.GetType().GetProperty("Content");
                            var c = cp?.GetValue(first)?.ToString() ?? "";
                            Log($"[探针B #{_hits}] ⑤ Content 长度 = {c.Length}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log($"[探针B #{_hits}] ③ 异常 {e.GetType().Name}: {e.Message}");
                    }
                }
            }
            catch (Exception e) { Log($"[探针B] Prefix 异常 {e.GetType().Name}: {e.Message}"); }
        }

        internal static void Log(string m)
        {
            try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(m); } catch { }
        }
    }
}
