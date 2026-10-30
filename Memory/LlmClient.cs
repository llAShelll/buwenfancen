using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BuWenFanChen.Memory;

/// <summary>
/// 抽取流程用的 LLM 客户端（复用玩家在游戏里配好的 API）。
/// 纯 .NET HTTP，不碰 Unity 对象，可在后台线程安全运行。
/// </summary>
public static class LlmClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public sealed class Config
    {
        public string Url;
        public string ApiKey;
        public string Model;
        public bool IsValid => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
    }

    /// <summary>
    /// 发一次 chat completion，返回 content 文本（失败返回 null + 错误信息）。
    /// ★ 2026-10-26：thinking 参数自适应——部分模型/中转不认 DeepSeek 的 thinking 字段，
    ///   会直接 400。这里先带 thinking 发，判断是参数问题就自动去掉重试一次。
    /// </summary>
    public static async Task<(string Content, string Error)> CompleteAsync(
        Config cfg, string systemPrompt, string userPrompt, int maxTokens = 3000)
    {
        if (cfg == null || !cfg.IsValid) return (null, "配置无效（缺 Url 或 ApiKey）");

        var (content, err) = await SendAsync(cfg, systemPrompt, userPrompt, maxTokens, withThinking: true);
        if (content == null && LooksLikeBadParam(err))
            (content, err) = await SendAsync(cfg, systemPrompt, userPrompt, maxTokens, withThinking: false);
        return (content, err);
    }

    private static bool LooksLikeBadParam(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        return err.Contains("400") || err.Contains("422")
            || err.Contains("thinking", StringComparison.OrdinalIgnoreCase)
            || err.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || err.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || err.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 规范化 API 端点：玩家可能只填了 https://api.deepseek.com（不带路径），
    /// 直接 POST 会 404。这里自动补成 /v1/chat/completions（已带路径的不动）。
    /// ★ 2026-10-28 玩家反馈「好感判断 HTTP 404」修复。
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var u = url.Trim();
        if (u.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)) return u;
        u = u.TrimEnd('/');
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return u + "/chat/completions";
        return u + "/v1/chat/completions";
    }

    private static async Task<(string Content, string Error)> SendAsync(
        Config cfg, string systemPrompt, string userPrompt, int maxTokens, bool withThinking)
    {
        try
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                ["temperature"] = 0.3,
                ["max_tokens"] = maxTokens,
            };
            // 抽取/建卡要确定性结果，关思考（DeepSeek 格式；其它模型会忽略或拒收）
            if (withThinking) body["thinking"] = new { type = "disabled" };

            var json = JsonSerializer.Serialize(body);
            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");

            using var doc = JsonDocument.Parse(text);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return (null, "choices 为空");
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return (null, "content 为空");
            return (content, null);
        }
        catch (Exception e)
        {
            return (null, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
