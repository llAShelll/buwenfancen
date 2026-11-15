namespace BuWenFanChen.Output;

using System;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using BepInEx.Logging;

/// <summary>
/// ★ 2026-11-04：自动好感度的「主请求模式」解析器。
///
/// 背景：原来好感度是**单独发一次 LLM 请求**判断的（IntimacyJudge），每轮多 1200 输入 token。
/// 当年试过让主请求多吐一个 intimacy_delta，结论是「模型不肯吐」——
/// 但现在发现那个结论很可能是当年取错了地方（看的是被语音 MOD 洗过的输出，跟 opts 一样的坑）。
///
/// 现在复用 opts 的模式：主请求要求顶层给 "intimacy":{"delta":N,"reason":"…"}，
/// 从【原始输出】里解析出来，存进 IntimacyJudge 的 pending，下一轮生效。
///
/// 开关：PromptConfig.IntimacyInMainRequest（关掉就回到原来的独立请求）
/// </summary>
internal static class MainIntimacyProbe
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·好感");

    /// <summary>从原始输出里解析 intimacy，交给 IntimacyJudge 存 pending。</summary>
    internal static void ParseAndSubmit(string cfgId, string origin)
    {
        try
        {
            if (!PromptConfig.IntimacyInMainRequest) return;
            if (string.IsNullOrEmpty(cfgId) || string.IsNullOrEmpty(origin)) return;

            int delta = 0;
            string reason = "";
            bool found = false;

            // ① 正常 JSON 解析
            try
            {
                var node = JsonNode.Parse(origin);
                if (node is JsonObject root && root["intimacy"] is JsonObject it)
                {
                    found = true;
                    var dv = it["delta"];
                    if (dv != null)
                    {
                        try { delta = (int)Math.Round(dv.GetValue<double>()); }
                        catch { try { delta = int.Parse(dv.ToString()); } catch { } }
                    }
                    try { reason = it["reason"]?.GetValue<string>() ?? ""; } catch { }
                }
            }
            catch { }

            // ② 正则兜底（JSON 有瑕疵时）
            if (!found)
            {
                var m = Regex.Match(origin,
                    "\"intimacy\"\\s*:\\s*\\{[^}]*?\"delta\"\\s*:\\s*(-?\\d+)",
                    RegexOptions.Singleline);
                if (m.Success)
                {
                    found = true;
                    int.TryParse(m.Groups[1].Value, out delta);
                }
            }

            if (!found)
            {
                Log.LogInfo("[好感] 主请求里没有 intimacy 字段");
                return;
            }

            if (delta == 0)
            {
                Log.LogInfo("[好感] 主请求给出 0，不调整");
                return;
            }

            IntimacyJudge.SubmitFromMainRequest(cfgId, delta, reason);
        }
        catch (Exception e)
        {
            Log.LogWarning($"[好感] 主请求解析异常：{e.GetType().Name}: {e.Message}");
        }
    }
}
