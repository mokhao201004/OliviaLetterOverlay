using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace OliviaLetterOverlay;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OliviaLetterOverlay", "logs");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "diagnostic.log");
    private const int MaximumLogBytes = 1024 * 1024;

    public static void RegisterSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lock (Gate)
            {
                Secrets.Add(value.Trim());
            }
        }
    }

    public static string Redact(string text)
    {
        string[] secrets;
        lock (Gate)
        {
            secrets = Secrets.OrderByDescending(value => value.Length).ToArray();
        }

        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(secret), "[REDACTED]", StringComparison.Ordinal);
        }

        text = Regex.Replace(text, "https?://[^\\s\"<>]+", match => SafeEndpoint(match.Value), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,""'}]+", "Bearer [REDACTED]");
        text = Regex.Replace(text, @"(?i)(\b(?:api[-_]?key|access[-_]?token|token|secret|password)\b[""']?\s*[:=]\s*[""']?)[^\s,""'}]+", "$1[REDACTED]");
        text = Regex.Replace(text, @"\bsk-[A-Za-z0-9_-]{8,}\b", "[REDACTED]");
        var userPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userPath))
        {
            text = text.Replace(userPath.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(userPath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    public static string SafeEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[invalid endpoint]";
        }

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    public static void Write(string area, string message)
    {
        try
        {
            var line = Redact($"{DateTimeOffset.Now:O} [{area}] {message}").Replace('\r', ' ').Replace('\n', ' ');
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    File.Move(LogPath, Path.Combine(DirectoryPath, "diagnostic.previous.log"), true);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic write must not interrupt an API call or a download.
        }
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        string operation, string? model = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        RegisterSecret(request.Headers.Authorization?.Parameter);
        if (request.Headers.TryGetValues("api-key", out var keys))
        {
            foreach (var key in keys)
            {
                RegisterSecret(key);
            }
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var timer = Stopwatch.StartNew();
        Write(operation, $"id={id} start {request.Method} {SafeEndpoint(request.RequestUri?.ToString())} model={model ?? "-"}");
        try
        {
            var response = await client.SendAsync(request, completion, cancellationToken);
            Write(operation, $"id={id} HTTP={(int)response.StatusCode} elapsed_ms={timer.ElapsedMilliseconds}");
            return response;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            Write(operation, $"id={id} {exception.GetType().Name} elapsed_ms={timer.ElapsedMilliseconds} {exception.Message}");
            throw;
        }
    }

    public static void Export(string destination)
    {
        RegisterSecret(AiProviderStore.GetMimoApiKey());
        foreach (var provider in CloudProviderCatalog.Providers)
        {
            RegisterSecret(AiProviderStore.GetCompatibleApiKey(new AiProviderSettings { CloudProviderId = provider.Id }));
        }

        var settings = AiProviderStore.Load();
        var modelsPath = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(modelsPath))
        {
            modelsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        }

        var report = new StringBuilder();
        report.AppendLine("Olivia Letter 诊断日志");
        report.AppendLine($"导出时间: {DateTimeOffset.Now:O}");
        report.AppendLine($"应用版本: {Assembly.GetExecutingAssembly().GetName().Version}");
        report.AppendLine($"系统: {Environment.OSVersion}; .NET: {Environment.Version}; 64位进程: {Environment.Is64BitProcess}");
        report.AppendLine($"服务类型: {settings.Provider}; 服务商: {settings.CloudProviderId}; 模型: {settings.Model}");
        report.AppendLine($"配置地址: {(settings.Provider == AiProviderKind.Mimo ? "MiMo 官方固定地址" : SafeEndpoint(settings.BaseUrl))}");
        report.AppendLine($"Ollama 模型目录（用户配置，运行中服务可能尚未更新）: {modelsPath}");
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            try
            {
                report.AppendLine($"磁盘 {drive.Name} 可用空间: {drive.AvailableFreeSpace / 1024d / 1024 / 1024:F2} GiB");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report.AppendLine($"磁盘 {drive.Name}: 无法查询可用空间");
            }
        }

        report.AppendLine("\n不包含 API Key、信件、记忆、人设、请求正文或完整响应正文；网址参数和用户目录已脱敏。");
        report.AppendLine("\n=== 应用诊断记录（每个文件最多 1 MiB，保留当前及上一份） ===");
        lock (Gate)
        {
            foreach (var file in new[] { "diagnostic.previous.log", "diagnostic.log" })
            {
                AppendTail(report, Path.Combine(DirectoryPath, file), MaximumLogBytes, false);
            }
        }

        report.AppendLine("\n=== Ollama 下载相关记录（不包含对话日志） ===");
        var ollamaDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama");
        foreach (var file in new[] { "server-2.log", "server-1.log", "server.log" })
        {
            AppendTail(report, Path.Combine(ollamaDirectory, file), 256 * 1024, true);
        }

        File.WriteAllText(destination, Redact(report.ToString()), new UTF8Encoding(true));
    }

    private static void AppendTail(StringBuilder report, string path, int maximumBytes, bool downloadsOnly)
    {
        report.AppendLine($"--- {Path.GetFileName(path)} ---");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, file.Length - maximumBytes);
            file.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            if (offset > 0)
            {
                reader.ReadLine();
            }

            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (downloadsOnly && !Regex.IsMatch(line, @"source=download\.go:|POST\s+""/api/pull""|pull model manifest", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                lines.Enqueue(line);
                if (lines.Count > (downloadsOnly ? 300 : 4000))
                {
                    lines.Dequeue();
                }
            }

            report.AppendLine(string.Join(Environment.NewLine, lines));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.AppendLine($"暂无可读取记录（{exception.GetType().Name}）");
        }
    }

    internal sealed class DownloadTrace : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _id = Guid.NewGuid().ToString("N")[..8];
        private readonly string _model;
        private readonly Timer _timer;
        private string _status = "starting";
        private string? _digest;
        private long _completed;
        private long _total;
        private long _lastAdvance = Stopwatch.GetTimestamp();
        private bool _finished;

        public DownloadTrace(string model)
        {
            _model = model;
            Write("download", $"id={_id} model={_model} start");
            _timer = new Timer(_ => Snapshot(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void Update(string? status, string? digest, long completed, long total)
        {
            lock (_gate)
            {
                if (digest != _digest || completed > _completed || status != _status)
                {
                    _lastAdvance = Stopwatch.GetTimestamp();
                }

                _status = status ?? _status;
                _digest = digest;
                _completed = completed;
                _total = total;
            }
        }

        private void Snapshot()
        {
            lock (_gate)
            {
                if (!_finished)
                {
                    Write("download", $"id={_id} model={_model} status={_status} completed={_completed} total={_total} no_progress_seconds={Stopwatch.GetElapsedTime(_lastAdvance).TotalSeconds:F0}");
                }
            }
        }

        public void Finish(string result)
        {
            lock (_gate)
            {
                _finished = true;
                Write("download", $"id={_id} model={_model} {result} completed={_completed} total={_total}");
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
