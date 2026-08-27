using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class OllamaClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl)
    {
        try
        {
            using var response = await Client.GetAsync(BuildUri(baseUrl, "/api/tags"));
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("本地模型服务还没准备好。请先安装并打开 Ollama，再刷新即可。");
            }

            await using var body = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(model => model.TryGetProperty("name", out var name) ? name.GetString()?.Trim() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("本地模型服务还没启动。先点击“下载 Ollama”安装并打开它，再刷新即可。", exception);
        }
    }

    public static async Task PullModelAsync(string baseUrl, string model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { model, stream = true });
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "/api/pull"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("模型下载没有开始。请先安装并打开 Ollama，再试一次。");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var update = JsonDocument.Parse(line);
                var root = update.RootElement;
                var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
                if (root.TryGetProperty("completed", out var completed) && root.TryGetProperty("total", out var total)
                    && completed.TryGetInt64(out var completedBytes) && total.TryGetInt64(out var totalBytes) && totalBytes > 0)
                {
                    progress?.Report($"{status ?? "正在下载"} · {completedBytes * 100 / totalBytes}%");
                }
                else if (!string.IsNullOrWhiteSpace(status))
                {
                    progress?.Report(status);
                }
            }
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("本地模型服务还没启动。先点击“下载 Ollama”安装并打开它，再下载模型。", exception);
        }
    }

    private static Uri BuildUri(string baseUrl, string path)
    {
        if (!AiProviderStore.IsHttpUrl(baseUrl))
        {
            throw new InvalidOperationException("本地 Ollama 地址必须是 http:// 或 https:// 开头的完整地址。");
        }

        return new Uri(AiProviderStore.NormalizeBaseUrl(baseUrl) + path);
    }
}
