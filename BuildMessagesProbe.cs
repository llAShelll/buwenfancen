// 探针 v3：找「真正发请求」的位置，并确认游戏用的是 tail 还是 doc 组装
//
// 修正 v2 的问题：
//   ① AccessTools.Method(t, "BuildMessages") 匹配失败 → 改用遍历 DeclaredMethods 按名字找
//   ② LlmLayerScanner 的 TargetMethod 返回 null → Harmony 整类跳过 → 改成有真实目标
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen
{
    /// <summary>
    /// 探针：hook BuildMessages（组装 messages 的地方），打印参数和返回。
    /// 用「名字匹配 + 取第一个」的方式找方法，避开参数签名匹配问题。
    /// </summary>
    [HarmonyPatch]
    internal static class BuildMessagesProbe
    {
        private static bool _logged;

        static MethodBase TargetMethod()
        {
            try
            {
                // 全程序集扫描，找任何叫 BuildMessages 且参数含 LlmMessage 的静态方法
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
                            Log($"[探针] 命中 {t.FullName}.{m.Name}");
                            Log($"[探针]   返回 {m.ReturnType.Name}");
                            Log($"[探针]   参数 ({sig})");
                            return m;
                        }
                    }
                }
                Log("[探针] 全程序集里没找到 BuildMessages(LlmMessage...) ");
            }
            catch (Exception e)
            {
                Log($"[探针] TargetMethod 异常 {e.GetType().Name}: {e.Message}");
            }
            return null;
        }

        [HarmonyPostfix]
        static void Postfix(object[] __args, object __result)
        {
            if (_logged) return;
            _logged = true;
            try
            {
                Log("========== BuildMessages 探针（只打一次）==========");
                if (__args != null)
                {
                    for (var i = 0; i < __args.Length; i++)
                    {
                        var a = __args[i];
                        if (a == null) { Log($"  __{i}: null"); continue; }
                        if (a is string str)
                        {
                            var head = str.Length > 50 ? str[..50] : str;
                            Log($"  __{i}: string {str.Length,5} 字  「{head.Replace("\n", "⏎")}」");
                        }
                        else if (a is IEnumerable en)
                        {
                            var n = 0;
                            foreach (var _ in en) n++;
                            Log($"  __{i}: 集合 {n} 项 ({a.GetType().Name})");
                        }
                        else Log($"  __{i}: {a.GetType().Name}");
                    }
                }

                Log("  --- 返回的 messages ---");
                if (__result is IEnumerable list)
                {
                    var idx = 0;
                    foreach (var msg in list)
                    {
                        idx++;
                        var role = ReadProp(msg, "Role") ?? "?";
                        var content = ReadProp(msg, "Content") ?? "";
                        var head = content.Length > 45 ? content[..45] : content;
                        Log($"    [{idx}] role={role}  {content.Length,5} 字  「{head.Replace("\n", "⏎")}」");
                    }
                    Log($"    共 {idx} 条");
                }
                Log("========== 探针结束 ==========");
            }
            catch (Exception e)
            {
                Log($"[探针] Postfix 异常 {e.GetType().Name}: {e.Message}");
            }
        }

        private static string ReadProp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null) return p.GetValue(o)?.ToString();
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (f != null) return f.GetValue(o)?.ToString();
            }
            catch { }
            return null;
        }

        internal static void Log(string m)
        {
            try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo(m); } catch { }
        }
    }

    /// <summary>
    /// 启动时扫一遍 LLM 层，打印 Complete / PostJsonAsync / FitChars 的类型和方法。
    /// 由 Plugin.cs 直接调用（不走 Harmony，避免 TargetMethod 返回 null 被跳过）。
    /// </summary>
    internal static class LlmLayerScan
    {
        public static string Run()
        {
            var outSb = new System.Text.StringBuilder();
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[扫描] ===== LLM 层类型清单 =====");
                var interesting = new[] { "Complete", "PostJsonAsync", "BuildMessages", "FitChars", "ExecuteAsync" };
                var found = 0;

                // 游戏主程序集 + Ainpc 运行时
                var asms = new List<Assembly>();
                try { asms.Add(typeof(Game.Model.NpcModel).Assembly); } catch { }
                try
                {
                    var t = AccessTools.TypeByName("JNGame.Ainpc.Llm.LlmMessage");
                    if (t != null) asms.Add(t.Assembly);
                }
                catch { }
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "A1Ainpc.Runtime" && !asms.Contains(a)) asms.Add(a);
                }

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
                                              BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        }
                        catch { continue; }

                        foreach (var m in ms)
                        {
                            if (!interesting.Contains(m.Name)) continue;
                            var sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                            if (!sig.Contains("LlmMessage") && !sig.Contains("CancellationToken") && !sig.Contains("LlmCallOptions")) continue;

                            found++;
                            sb.AppendLine($"[扫描]   {t.FullName}.{m.Name}  {(m.IsAbstract ? "[abstract]" : m.IsVirtual ? "[virtual]" : "")}");
                            sb.AppendLine($"[扫描]       返回 {m.ReturnType.Name}  参数 ({sig})");
                        }
                    }
                }
                sb.AppendLine($"[扫描] ===== 共 {found} 个候选 =====");
                outSb.Append(sb.ToString());
            }
            catch (Exception e)
            {
                outSb.Append($"[扫描] 异常 {e.GetType().Name}: {e.Message}");
            }
            return outSb.ToString();
        }
    }
}
