using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BuWenFanChen.Diagnostics;

/// <summary>
/// ★ 2026-11-12：把「游戏真正发给模型的那些 messages」落盘。
///
/// 为什么需要（今晚的坑）：
///   玩家装了首轮热补丁后第一轮仍然空响应（rawChars=0）。我们逐条排除了：
///     长度（只占 1M 上下文的 2%）、模型名（deepseek-flash 有效）、推理吃光（实测思考才 43~364 tok）。
///   剩下的唯一可能就是「HTTP 层失败」，但游戏把状态码吞了，日志里一个字都没有 ——
///   只能把请求体原样存下来，事后逐条比对（尤其看有没有空 content / 非法字符 / 结构异常）。
///
/// 落点选择：
///   `Game.Model.AinpcRuntime.LogRequest(NpcModel, ModeParams, IReadOnlyList&lt;LlmMessage&gt;, Plan, …)`
///   这是游戏把 messages 组装完、真正发出去之前的最后一个入口。
///   ⚠ 历史上 LLM 调用层（Complete / PostJsonAsync）在 Il2CppInterop 下 hook 会崩（0xc0000005），
///     但那个坑是「用 IEnumerable 遍历 + GetValue」造成的；这里改用 CanonStore 已经跑了
///     几十轮验证过的 `get_Item` 索引反射，全程 try/catch，读不到就整段跳过，不碰任何参数。
///
/// 产物：`{存档}\BuWenFanChen\_diag\requests\req_序号.json`
///   滚动保留最近 60 份，玩家直接把整个目录发过来就能定位。
/// 关闭：把 <see cref="Enabled"/> 改 false（或删掉 Plugin.cs 里的 PatchAll）。
/// </summary>
[HarmonyPatch]
internal static class RequestBodyDump
{
    /// <summary>总开关（诊断期默认开；确认问题后可关）</summary>
    internal static bool Enabled = true;

    /// <summary>最多保留多少份（滚动删最旧）</summary>
    private const int MaxFiles = 60;

    /// <summary>单条消息落盘上限（防止某条超长把文件撑爆；超过只存前 20,000 字）</summary>
    private const int MaxMsgChars = 20000;

    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·请求体");

    private static int _seq;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        try
        {
            var t = AccessTools.TypeByName("Game.Model.AinpcRuntime");
            if (t == null)
            {
                Log.LogWarning("[请求体] 找不到 Game.Model.AinpcRuntime → 请求体落盘不可用");
                return null;
            }

            MethodBase best = null;
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null || m.Name != "LogRequest") continue;
                var ps = m.GetParameters();
                // 目标签名第 3 个参数是 IReadOnlyList<LlmMessage>
                if (ps.Length < 3) continue;
                if (!ps[2].ParameterType.Name.Contains("IReadOnlyList")) continue;
                if (best == null || ps.Length > best.GetParameters().Length) best = m;
            }

            if (best == null)
            {
                Log.LogWarning("[请求体] 找不到 LogRequest(...IReadOnlyList...) 重载 → 请求体落盘不可用");
                return null;
            }

            var sb = new StringBuilder("[请求体] 命中 ");
            sb.Append(best.DeclaringType?.FullName).Append('.').Append(best.Name).Append('(');
            var list = best.GetParameters();
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(list[i].ParameterType.Name).Append(' ').Append(list[i].Name);
            }
            sb.Append(')');
            Log.LogInfo(sb.ToString());
            return best;
        }
        catch (Exception e)
        {
            Log.LogWarning("[请求体] TargetMethod 失败：" + e.GetType().Name + " " + e.Message);
            return null;
        }
    }

    [HarmonyPostfix]
    internal static void Postfix(object[] __args)
    {
        if (!Enabled) return;
        try
        {
            if (__args == null || __args.Length == 0) return;

            // 先打 Plan 的预算判定（★ 2026-11-12：玩家报 ContextBudgetExceeded 时，答案就在这几个数上）
            ReportPlan(__args);

            // 从参数里挑出「消息列表」：不是 string、能用 get_Item 索引、元素有 get_Content
            object list = null;
            int listIdx = -1;
            for (int i = 0; i < __args.Length; i++)
            {
                var a = __args[i];
                if (a == null || a is string) continue;
                var n = BuWenFanChen.History.CanonStore.CountItems(a);
                if (n <= 0) continue;
                var itemM = a.GetType().GetMethod("get_Item");
                if (itemM == null) continue;
                object first = null;
                try { first = itemM.Invoke(a, new object[] { 0 }); } catch { }
                if (first == null) continue;
                if (first.GetType().GetMethod("get_Content") == null) continue;
                list = a; listIdx = i; break;
            }
            if (list == null) return;

            Dump(list);

            // 空 content 是 400 的高嫌疑：单独报警（只报第一条，别刷屏）
            WarnEmptyMessages(list);
        }
        catch (Exception e)
        {
            try
            {
                Log.LogWarning("[请求体] 落盘失败：" + e.GetType().Name + " " + e.Message);
                Enabled = false;   // 一旦出问题立刻自我关闭，别影响游戏
            }
            catch { }
        }
    }

    /// <summary>
    /// ★ 2026-11-12：把 Plan 的预算判定打出来。
    ///
    ///   玩家报的 `LlmError.ContextBudgetExceeded / "generated content exceeds budget"`
    ///   是**游戏自己**在发请求前判的（HttpCode=0、PromptTokens=0，请求根本没出去）。
    ///   Plan 上有 CanSend / EstimatedTokens / TotalChars / TrimReason —— 答案就在这几个数上。
    ///   这是唯一能看到「请求即将发出那一刻」真实预算的地方。
    /// </summary>
    private static void ReportPlan(object[] __args)
    {
        // ★ 2026-11-12 修（PRO 审核指出「探针从没打到过判定点」）：
        //   旧写法用 `GetType().GetProperty("CanSend")` 去**认** Plan，但 Harmony 传进来的
        //   参数是 Il2Cpp 包装对象，类型名匹配不上 → 一直静默跳过 → 一次 Plan 数值都没打出来。
        //   现在：签名已实测确认 Plan 在第 4 个参数（index 3），直接取；属性/字段双查兜底。
        object plan = null;
        if (__args.Length > 3 && __args[3] != null)
        {
            try
            {
                var t3 = __args[3].GetType();
                if (t3.GetProperty("CanSend") != null || t3.GetField("CanSend") != null
                    || t3.GetProperty("EstimatedTokens") != null || t3.GetField("EstimatedTokens") != null)
                    plan = __args[3];
            }
            catch { }
        }
        if (plan == null)
        {
            // 兜底：真出意外时按特征再找一遍（保留旧逻辑）
            for (int i = 0; i < __args.Length; i++)
            {
                if (__args[i] == null || __args[i] is string) continue;
                try
                {
                    var tt = __args[i].GetType();
                    if (tt.GetProperty("CanSend") != null || tt.GetField("CanSend") != null)
                    { plan = __args[i]; break; }
                }
                catch { }
            }
        }
        if (plan == null)
        {
            Log.LogWarning("[请求体] 第 4 个参数不是 Plan（拿不到 CanSend）→ 预算判定点仍未观测到");
            return;
        }

        string purpose = "", model = "";
        int attempt = -1;
        try { if (__args.Length > 6) purpose = __args[6]?.ToString() ?? ""; } catch { }
        try { if (__args.Length > 7 && __args[7] != null) attempt = Convert.ToInt32(__args[7]); } catch { }
        try { if (__args.Length > 8) model = __args[8]?.ToString() ?? ""; } catch { }

        string canSend = PStr(plan, "CanSend");
        int est = PInt(plan, "EstimatedTokens");
        int total = PInt(plan, "TotalChars");
        int fixedC = PInt(plan, "FixedChars");
        int dropped = PInt(plan, "DroppedHistoryCount");
        string trim = PStr(plan, "TrimReason");

        var line = $"[请求体] Plan：CanSend={canSend} EstimatedTokens={est} TotalChars={total}"
                 + $" FixedChars={fixedC} 丢历史={dropped} TrimReason={trim}"
                 + $" | purpose={purpose} attempt={attempt} model={model}";
        if (canSend == "False")
            Log.LogWarning(line + "   ★★ 游戏会拒绝发送（ContextBudgetExceeded）");
        else
            Log.LogInfo(line);
    }

    // ★ 2026-11-12：字段/属性双查（游戏私有成员常是字段，GetProperty 找不到就查字段；
    //   两者都要带 NonPublic —— Plan 的 CanSend 在 interop 里是公有的，但 TrimReason 之类不保证）
    private const System.Reflection.BindingFlags AnyFlag =
        System.Reflection.BindingFlags.Instance
        | System.Reflection.BindingFlags.Public
        | System.Reflection.BindingFlags.NonPublic;

    private static string PStr(object o, string name)
    {
        try
        {
            var t = o.GetType();
            var p = t.GetProperty(name, AnyFlag);
            if (p != null) { var v = p.GetValue(o); return v == null ? "null" : v.ToString(); }
            var f = t.GetField(name, AnyFlag);
            if (f != null) { var v = f.GetValue(o); return v == null ? "null" : v.ToString(); }
            return "?";
        }
        catch { return "?"; }
    }

    private static int PInt(object o, string name)
    {
        try
        {
            var t = o.GetType();
            var p = t.GetProperty(name, AnyFlag);
            if (p != null) { var v = p.GetValue(o); return v == null ? -1 : Convert.ToInt32(v); }
            var f = t.GetField(name, AnyFlag);
            if (f != null) { var v = f.GetValue(o); return v == null ? -1 : Convert.ToInt32(v); }
            return -1;
        }
        catch { return -1; }
    }

    private static void Dump(object list)
    {
        var itemM = list.GetType().GetMethod("get_Item");
        if (itemM == null) return;

        var sb = new StringBuilder(32 * 1024);
        sb.Append("{\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",");
        int n = 0, total = 0, maxLen = 0;
        var body = new StringBuilder();

        for (int i = 0; i < 5000; i++)
        {
            object it;
            try { it = itemM.Invoke(list, new object[] { i }); }
            catch { break; }
            if (it == null) break;

            string role = "", content = "";
            try { role = it.GetType().GetMethod("get_Role")?.Invoke(it, null)?.ToString() ?? ""; } catch { }
            try { content = it.GetType().GetMethod("get_Content")?.Invoke(it, null)?.ToString() ?? ""; } catch { }

            if (content.Length > maxLen) maxLen = content.Length;
            total += content.Length;

            if (body.Length > 0) body.Append(',');
            body.Append("{\"i\":").Append(i)
                .Append(",\"role\":").Append(JsonStr(role))
                .Append(",\"chars\":").Append(content.Length)
                .Append(",\"content\":").Append(JsonStr(
                    content.Length > MaxMsgChars ? content.Substring(0, MaxMsgChars) + "…[截断]" : content))
                .Append('}');
            n++;
        }

        sb.Append("\"count\":").Append(n)
          .Append(",\"totalChars\":").Append(total)
          .Append(",\"maxChars\":").Append(maxLen)
          .Append(",\"messages\":[").Append(body).Append("]}");

        var dir = System.IO.Path.Combine(BuWenFanChen.Memory.MemoryPaths.Root, "_diag", "requests");
        System.IO.Directory.CreateDirectory(dir);

        _seq++;
        var name = "req_" + _seq.ToString("D4") + "_"
                 + DateTime.Now.ToString("HHmmss") + ".json";
        var path = System.IO.Path.Combine(dir, name);
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        Log.LogInfo($"[请求体] 落盘 {name}：{n} 条 / {total:N0} 字（最长 {maxLen:N0}）"
                    + $" 目录={dir}");

        CleanupOld(dir);
    }

    private static void WarnEmptyMessages(object list)
    {
        try
        {
            var itemM = list.GetType().GetMethod("get_Item");
            if (itemM == null) return;
            int empties = 0;
            string firstInfo = null;
            for (int i = 0; i < 200; i++)
            {
                object it;
                try { it = itemM.Invoke(list, new object[] { i }); }
                catch { break; }
                if (it == null) break;
                string role = "", content = null;
                try { role = it.GetType().GetMethod("get_Role")?.Invoke(it, null)?.ToString() ?? ""; } catch { }
                try { content = it.GetType().GetMethod("get_Content")?.Invoke(it, null)?.ToString(); } catch { }
                if (string.IsNullOrWhiteSpace(content))
                {
                    empties++;
                    firstInfo ??= $"第{i}条 role={role}";
                }
            }
            if (empties > 0)
            {
                Log.LogWarning($"[请求体] ★ 有 {empties} 条消息 content 为空（{firstInfo}）"
                               + " —— 这是 API 返回 400 的高嫌疑，优先查这里");
            }
        }
        catch { }
    }

    private static void CleanupOld(string dir)
    {
        try
        {
            var files = new List<string>(System.IO.Directory.GetFiles(dir, "req_*.json"));
            if (files.Count <= MaxFiles) return;
            files.Sort(StringComparer.OrdinalIgnoreCase);
            int del = files.Count - MaxFiles;
            for (int i = 0; i < del; i++)
            {
                try { System.IO.File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }

    private static string JsonStr(string s)
    {
        if (s == null) return "\"\"";
        try { return System.Text.Json.JsonSerializer.Serialize(s); }
        catch { return "\"\""; }
    }
}
