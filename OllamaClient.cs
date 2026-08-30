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
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, "/api/tags"));
            using var response = await DiagnosticLog.SendAsync(Client, request, "ollama.models");
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
        using var trace = new DiagnosticLog.DownloadTrace(model);
        try
        {
            var payload = JsonSerializer.Serialize(new { model, stream = true });
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "/api/pull"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await DiagnosticLog.SendAsync(Client, request, "ollama.pull", model, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"模型下载失败（HTTP {(int)response.StatusCode}）：{DiagnosticLog.Redact(detail)}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw new IOException("下载连接已结束，但没有收到完成确认。可导出日志排查后重试。");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var update = JsonDocument.Parse(line);
                var root = update.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException($"模型下载失败：{DiagnosticLog.Redact(error.ToString())}");
                }

                var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
                var digest = root.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
                var completedBytes = root.TryGetProperty("completed", out var completed) && completed.TryGetInt64(out var done) ? done : 0;
                var totalBytes = root.TryGetProperty("total", out var total) && total.TryGetInt64(out var size) ? size : 0;
                trace.Update(status, digest, completedBytes, totalBytes);
                if (status == "success")
                {
                    trace.Finish("success");
                    progress?.Report("下载完成");
                    return;
                }

                if (totalBytes > 0)
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
            trace.Finish($"failed {exception.GetType().Name}: {exception.Message}");
            throw new InvalidOperationException("本地模型服务还没启动。先点击“下载 Ollama”安装并打开它，再下载模型。", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            trace.Finish($"failed {exception.GetType().Name}: {exception.Message}");
            throw;
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
