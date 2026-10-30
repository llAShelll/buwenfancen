// 探针：hook JNGame.Ainpc.Llm.OpenAiCompatProvider.Complete
//
// 这是「messages 已定型、还没序列化」的位置 —— 对应 1.1.2 在 HTTP 层代理做的事。
// 本次只观察（不改），打印最终 messages 数组，确认：
//   ① 我们的 tail 改动（【最高指令】+【字数】+【系统死命令】）到底有没有进 messages
//   ② messages 的条数、角色分布、末尾几条是什么
//   ③ 玩家这句话（input）在哪一条
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen
{
    [HarmonyPatch]
    internal static class LlmCompleteProbe
    {
        private static int _logged;

        static MethodBase TargetMethod()
        {
            try
            {
                var t = AccessTools.TypeByName("JNGame.Ainpc.Llm.OpenAiCompatProvider");
                if (t == null)
                {
                    Log("[探针2] 没找到 OpenAiCompatProvider 类型");
                    return null;
                }
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Complete") continue;
                    var sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                    // ★ 参数类型名是 IReadOnlyList`1，泛型参数 LlmMessage 不体现在 Name 里
                    //   所以判断条件要放宽：只要有 IReadOnlyList 且带 LlmCallOptions 就是目标
                    if (!sig.Contains("IReadOnlyList") || !sig.Contains("LlmCallOptions")) continue;
                    Log($"[探针2] 命中 {t.FullName}.Complete  参数({sig})");
                    return m;
                }
                Log("[探针2] OpenAiCompatProvider 里没有 Complete(LlmMessage...)");
            }
            catch (Exception e)
            {
                Log($"[探针2] TargetMethod 异常 {e.GetType().Name}: {e.Message}");
            }
            return null;
        }

        [HarmonyPrefix]
        static void Prefix(object __0)
        {
            if (_logged >= 3) return;    // 只看前 3 次，避免刷屏
            _logged++;
            try
            {
                var messages = __0 as IEnumerable;
                if (messages == null)
                {
                    Log($"[探针2] 参数0 不是集合：{__0?.GetType().Name ?? "null"}");
                    return;
                }

                Log($"========== Complete 探针 #{_logged} ==========");
                var idx = 0;
                var all = new System.Text.StringBuilder();
                foreach (var msg in messages)
                {
                    idx++;
                    var role = ReadProp(msg, "Role") ?? "?";
                    var content = ReadProp(msg, "Content") ?? "";
                    var head = content.Length > 60 ? content[..60] : content;
                    var line = $"  [{idx}] role={role}  {content.Length,5} 字  「{head.Replace("\n", "⏎")}」";
                    Console.WriteLine(line);
                    all.AppendLine(line);
                }
                Log(all.ToString());
                Log($"  共 {idx} 条 messages");

                // 关键判定：末尾几条里有没有我们的标记
                var joined = ReadAll(messages);
                Log($"  含【系统死命令】: {joined.Contains("【系统死命令】")}");
                Log($"  含【最高指令】:   {joined.Contains("【最高指令】")}");
                Log($"  含【字数】:       {joined.Contains("【字数】")}");
                Log("========== Complete 探针结束 ==========");
            }
            catch (Exception e)
            {
                Log($"[探针2] Prefix 异常 {e.GetType().Name}: {e.Message}");
            }
        }

        private static string ReadAll(IEnumerable messages)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in messages) sb.Append(ReadProp(m, "Content") ?? "");
            return sb.ToString();
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
}
