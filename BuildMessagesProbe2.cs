// 探针 C：验证 BuildMessages 能否安全 hook + ref 改返回（方案 B 路 1 前置验证）
//
// 背景：Complete 崩（il2cpp_value_box），但 BuildMessages 之前探针跑通过（不崩）。
// 本探针只做两件事：
//   ① 读返回 messages 的条数 + 第一条 role（确认能读）
//   ② 用 ref 拿住 __result（确认将来能改返回）
// 不真的改 messages，避免副作用。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen
{
    [HarmonyPatch]
    internal static class BuildMessagesProbe2
    {
        private static int _hits;

        static MethodBase TargetMethod()
        {
            try
            {
                var asms = new List<Assembly>();
                try { asms.Add(typeof(Game.Model.NpcModel).Assembly); } catch { }
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "A1Ainpc.Runtime" && !asms.Contains(a)) asms.Add(a);

                foreach (var asm in asms.Distinct())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException re) { types = re.Types.Where(x => x != null).ToArray(); }
                    catch { continue; }
                    foreach (var t in types)
                    {
                        MethodInfo[] ms;
                        try
                        {
                            ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        }
                        catch { continue; }
                        foreach (var m in ms)
                        {
                            if (m.Name != "BuildMessages") continue;
                            var sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                            if (!sig.Contains("LlmMessage")) continue;
                            Log($"[探针C] 命中 {t.FullName}.BuildMessages");
                            Log($"[探针C]   返回 {m.ReturnType.FullName}");
                            Log($"[探针C]   参数 ({sig})");
                            return m;
                        }
                    }
                }
                Log("[探针C] 没找到 BuildMessages(LlmMessage...)");
            }
            catch (Exception e) { Log($"[探针C] TargetMethod 异常 {e.GetType().Name}: {e.Message}"); }
            return null;
        }

        [HarmonyPostfix]
        static void Postfix(object[] __args, ref object __result)
        {
            if (_hits >= 2) return;
            _hits++;
            try
            {
                Log($"[探针C #{_hits}] ① 返回类型 = {__result?.GetType().FullName ?? "null"}");
                if (__result == null) return;

                // ② 读条数（优先 Count 属性）
                int cnt = -1;
                try
                {
                    var p = __result.GetType().GetProperty("Count");
                    if (p != null) cnt = Convert.ToInt32(p.GetValue(__result));
                    else if (__result is IEnumerable en) { foreach (var _ in en) cnt++; }
                    Log($"[探针C #{_hits}] ② 条数 = {cnt}");
                }
                catch (Exception e) { Log($"[探针C #{_hits}] ② 读条数异常 {e.GetType().Name}: {e.Message}"); }

                // ③ 读第一条 role
                if (cnt > 0)
                {
                    try
                    {
                        object first = null;
                        var idx = __result.GetType().GetProperty("Item");
                        if (idx != null) first = idx.GetValue(__result, new object[] { 0 });
                        else if (__result is IEnumerable en) { var e = en.GetEnumerator(); if (e.MoveNext()) first = e.Current; }
                        Log($"[探针C #{_hits}] ③ [0] 类型 = {first?.GetType().FullName ?? "null"}");
                        if (first != null)
                        {
                            var rp = first.GetType().GetProperty("Role");
                            Log($"[探针C #{_hits}] ④ [0].Role = {rp?.GetValue(first)}");
                        }
                    }
                    catch (Exception e) { Log($"[探针C #{_hits}] ③ 异常 {e.GetType().Name}: {e.Message}"); }
                }
            }
            catch (Exception e) { Log($"[探针C] Postfix 异常 {e.GetType().Name}: {e.Message}"); }
        }

        internal static void Log(string m)
        {
            try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(m); } catch { }
        }
    }
}
