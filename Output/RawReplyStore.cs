using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BuWenFanChen.Output;

/// <summary>
/// ★ 2026-11-04：把「模型原始输出」截下来。
///
/// 为什么需要：实测发现语音 MOD（A1-TTS-Mod）在 AddNpcChatMessage 之前会做一次
/// HistoryEnvelope.NormalizeOrWrap，把 a1ttsv2 和我们的 opts 一起剥掉
/// （TTS 日志：502 字 a1ttsv2=valid → 264 字 a1ttsv2=absent）。
/// 所以 AddNpcChatMessage 的 npcRawOutput 已经是洗过的版本，永远看不到 opts。
///
/// LogResponse 早于 AddNpcChatMessage，且能拿到 LlmResult.Content（= 原始输出），
/// 这就是唯一的入口。只存「JSON 形态」的那条（ActualReply），摘要调用那种纯文本不存。
///
/// 只读，不改任何参数。
/// </summary>
[HarmonyPatch(typeof(Game.Model.AinpcRuntime), "LogResponse")]
internal static class RawReplyStore
{
    private static string _last;
    private static readonly object Gate = new();

    [HarmonyPostfix]
    internal static void Postfix(object __2)
    {
        try
        {
            if (__2 == null) { WarnEmpty("结果对象为空", null); return; }
            ProbePropsOnce(__2);
            var c = __2.GetType().GetProperty("Content")?.GetValue(__2) as string;
            if (string.IsNullOrWhiteSpace(c)) { WarnEmpty("content 为空", __2); return; }
            var trimmed = c.TrimStart();

            // ★ 2026-11-07 诊断（主人要求"别瞎猜"）：
            //   之前这里 `if (!StartsWith("{")) return;` 直接把「纯文本形态」丢掉了——
            //   而掉 TTS 那几轮恰好可能就是纯文本/坏 JSON，结果永远看不到原文。
            //   现在不管什么形态都落盘一份，崩了直接翻文件。
            //   落盘条件：以 { 开头（正常 JSON / 坏 JSON）或长度异常短（<400，崩的典型特征）。
            //   纯文本长摘要（>400 且不以 { 开头）不落，避免污染。
            try
            {
                if (trimmed.StartsWith("{") || c.Length < 400) DumpRaw(c);
            }
            catch { }

            // 缓存语义保持原样：只留 JSON 对象形态给 opts / intimacy 解析
            if (!trimmed.StartsWith("{")) return;
            lock (Gate) _last = c;
        }
        catch { }
    }

    /// <summary>
    /// ★ 2026-11-11：空响应警报。
    ///   玩家报「上下文超出模型上限」时，真正发生的事常常是**这一次 API 返回了空内容**
    ///   （HTTP 400 / 限流 / 超时），游戏把任何失败都显示成那句话。
    ///   以前这里对空响应直接 return，什么都不记 —— 所以事后只能靠 TTS MOD 的 rawChars=0 反推。
    ///   现在自己记一条，和上一行 `[请求]` 的估算大小对着看就能定性。
    /// </summary>
    private static int _emptyCount;

    private static void WarnEmpty(string why, object result)
    {
        try
        {
            _emptyCount++;
            var src = BepInEx.Logging.Logger.CreateLogSource("不问凡尘·响应");
            src.LogWarning($"[响应] ★★ 空响应（{why}）累计第 {_emptyCount} 次 —— "
                           + "可能是 HTTP 400 / 限流 / 超时；对照上面最近一条 [请求] 的估算大小与占用率，"
                           + "占用率正常就说明不是「上下文太长」");
            // ★ 2026-11-12：把 LlmResult 的全部字段打出来 —— 这是唯一能区分
            //   「内容为空（finish_reason=length / 审核）」和「HTTP 层失败（error）」的地方。
            if (result != null)
            {
                var detail = DescribeResult(result);
                if (!string.IsNullOrEmpty(detail)) src.LogWarning("[响应] LlmResult 全字段：" + detail);
                DumpEmptyResult(result);
            }
        }
        catch { }
    }

    // ============ ★ 2026-11-12：LlmResult 字段探针 ============
    //
    // 背景：玩家装了首轮热补丁后第一轮仍然空响应（rawChars=0）。
    //   已排除长度（占 1M 上下文的 2%）、模型名（deepseek-flash 有效）、推理吃光（实测思考才 43~364 tok）。
    //   剩下的只有「HTTP 层失败」，而游戏把状态码吞了 —— 只能从 LlmResult 上挖。
    //   做法：① 启动后第一次拿到 LlmResult 时打一份完整属性清单（只打一次，不刷屏）
    //        ② 每次空响应把全字段打进日志 + 追加落盘 `_diag\empty_response.log`（玩家可以直接发文件）
    //   注意：反射读 Il2Cpp 对象一律 try/catch —— 读不到只丢一个字段，不影响游戏。

    private static bool _propsProbed;

    private static readonly string[] SkipProps = { "Pointer", "ObjectClass", "WasCollected" };

    private static void ProbePropsOnce(object r)
    {
        if (_propsProbed) return;
        _propsProbed = true;
        try
        {
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘·响应")
                .LogInfo($"[响应] LlmResult 类型={r.GetType().FullName} 字段清单：{DescribeResult(r)}");
        }
        catch { }
    }

    /// <summary>把对象的可读属性拼成一行 `名(类型)=值 | …`（读不到写异常名，绝不抛）。</summary>
    private static string DescribeResult(object r)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var p in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var skip = false;
                foreach (var s in SkipProps) if (p.Name == s) { skip = true; break; }
                if (skip) continue;

                string v;
                try
                {
                    var val = p.GetValue(r);
                    v = val == null ? "null" : SafeStr(val);
                }
                catch (Exception e) { v = "<" + e.GetType().Name + ">"; }

                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(p.Name).Append('(').Append(p.PropertyType.Name).Append(")=").Append(v);
            }
        }
        catch { }
        return sb.ToString();
    }

    private static string SafeStr(object o)
    {
        try
        {
            var s = o.ToString();
            if (s == null) return "null";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }
        catch { return "?"; }
    }

    private static void DumpEmptyResult(object r)
    {
        try
        {
            var dir = System.IO.Path.Combine(BuWenFanChen.Memory.MemoryPaths.Root, "_diag");
            System.IO.Directory.CreateDirectory(dir);
            var p = System.IO.Path.Combine(dir, "empty_response.log");
            var sb = new StringBuilder();
            sb.Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append(" 空响应 #").Append(_emptyCount).Append(" ====\n");
            sb.Append("类型: ").Append(r.GetType().FullName).Append('\n');
            sb.Append("字段: ").Append(DescribeResult(r)).Append('\n');
            // 连字段也扫一遍（部分 error 信息可能是字段而不是属性）
            try
            {
                foreach (var f in r.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    string v;
                    try
                    {
                        var val = f.GetValue(r);
                        v = val == null ? "null" : SafeStr(val);
                    }
                    catch (Exception e) { v = "<" + e.GetType().Name + ">"; }
                    sb.Append("  field ").Append(f.Name).Append('(').Append(f.FieldType.Name).Append(")=").Append(v).Append('\n');
                }
            }
            catch { }
            sb.Append('\n');
            System.IO.File.AppendAllText(p, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // ★ 2026-11-07：原始输出滚动落盘（诊断用，可随时删）
    //   路径：{存档}\BuWenFanChen\_diag\raw_output.jsonl
    private static void DumpRaw(string c)
    {
        try
        {
            var dir = System.IO.Path.Combine(BuWenFanChen.Memory.MemoryPaths.Root, "_diag");
            System.IO.Directory.CreateDirectory(dir);
            var p = System.IO.Path.Combine(dir, "raw_output.jsonl");
            var line = "{\"ts\":\"" + DateTime.Now.ToString("MM-dd HH:mm:ss")
                     + "\",\"len\":" + c.Length
                     + ",\"startsBrace\":" + (c.TrimStart().StartsWith("{") ? "true" : "false")
                     + ",\"raw\":" + System.Text.Json.JsonSerializer.Serialize(c) + "}\n";
            System.IO.File.AppendAllText(p, line, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>取走并清空（一对一消费，避免下一轮误用上一轮）。</summary>
    internal static string Take()
    {
        lock (Gate)
        {
            var r = _last;
            _last = null;
            return r;
        }
    }

    /// <summary>只读当前缓存的原始输出（不清空）—— 给多个解析器共用
    /// （opts 和 intimacy 要读同一份）。</summary>
    internal static string Peek()
    {
        lock (Gate) return _last;
    }

    /// <summary>本轮处理完统一清空（在 AddNpcChatMessage 末尾调）。</summary>
    internal static void Clear()
    {
        lock (Gate) _last = null;
    }
}
