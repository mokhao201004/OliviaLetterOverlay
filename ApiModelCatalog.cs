using System.Net.Http;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class ApiModelCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static IReadOnlyList<string> Presets { get; } =
    [
        "gpt-4o-mini",
        "gpt-4o",
        "gpt-4.1-mini",
        "gpt-4.1",
        "o4-mini",
        "claude-3-5-haiku-latest",
        "claude-3-7-sonnet-latest",
        "claude-sonnet-4",
        "gemini-2.0-flash",
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "deepseek-chat",
        "deepseek-reasoner",
        "qwen-plus",
        "qwen-max",
        "glm-4.5-air",
        "glm-4.5",
        "kimi-k2-instruct",
    ];

    public static async Task<IReadOnlyList<string>> ListOpenAiCompatibleModelsAsync(string baseUrl, string? apiKey)
    {
        if (!AiProviderStore.IsHttpUrl(baseUrl))
        {
            throw new InvalidOperationException("请先填写正确的 Base URL。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(baseUrl));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Headers.Add("api-key", apiKey.Trim());
        }

        using var response = await Client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = response.ReasonPhrase ?? string.Empty;
            }

            detail = detail.Length > 180 ? detail[..180] + "…" : detail;
            throw new InvalidOperationException($"获取模型列表失败（HTTP {(int)response.StatusCode}）：{detail}");
        }

        await using var body = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(body);
        var root = document.RootElement;
        var candidates = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) ? data
            : root.TryGetProperty("models", out var models) ? models
            : default;

        if (candidates.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("接口返回了模型数据，但格式不是标准列表。可直接填写模型名。");
        }

        var names = candidates.EnumerateArray()
            .Select(ReadModelName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            throw new InvalidOperationException("接口没有返回可用模型。可直接填写模型名。");
        }

        return names;
    }

    private static string BuildModelsUrl(string baseUrl)
    {
        var url = AiProviderStore.NormalizeBaseUrl(baseUrl);
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/chat/completions".Length];
        }

        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? url + "/models"
            : url + "/v1/models";
    }

    private static string? ReadModelName(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString()?.Trim();
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in new[] { "id", "model", "name" })
        {
            if (item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var name = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return null;
    }
}
