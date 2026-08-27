using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace OliviaLetterOverlay;

internal static class OllamaInstaller
{
    private const string OfficialInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const long ParallelDownloadThresholdBytes = 2 * 1024 * 1024;
    private const long MinimumSegmentSize = 8 * 1024 * 1024;
    private const int MaximumSegments = 12;
    private static readonly TimeSpan ZeroProgressTimeout = TimeSpan.FromSeconds(40);
    private static readonly string InstallerDirectory = ResolveInstallerDirectory();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static async Task<string> DownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(InstallerDirectory);
        var installerPath = Path.Combine(InstallerDirectory, "OllamaSetup.exe");
        var temporaryPath = installerPath + ".download";

        try
        {
            if (!await TryDownloadInParallelAsync(temporaryPath, progress, cancellationToken))
            {
                await DownloadSingleStreamAsync(temporaryPath, progress, cancellationToken);
            }

            File.Move(temporaryPath, installerPath, true);
            return installerPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static async Task<bool> TryDownloadInParallelAsync(string temporaryPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var probeRequest = CreateRangeRequest(0, 0);
        using var probeResponse = await Client.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var totalLength = probeResponse.Content.Headers.ContentRange?.Length;
        if (!probeResponse.IsSuccessStatusCode
            || probeResponse.StatusCode != System.Net.HttpStatusCode.PartialContent
            || totalLength is not > ParallelDownloadThresholdBytes)
        {
            return false;
        }

        var totalBytes = totalLength.Value;
        var segmentCount = (int)Math.Clamp(
            (long)Math.Ceiling(totalBytes / (double)MinimumSegmentSize),
            2,
            MaximumSegments);
        var segmentSize = totalBytes / segmentCount;
        await using (var target = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 128,
                         useAsync: true))
        {
            target.SetLength(totalBytes);
        }

        var counter = new DownloadCounter(totalBytes);
        var downloads = new List<Task>();
        using var downloadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var zeroProgressCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(downloadCancellationTokenSource.Token);
        using var zeroProgressTimer = new Timer(
            _ => zeroProgressCancellationTokenSource.Cancel(),
            null,
            ZeroProgressTimeout,
            Timeout.InfiniteTimeSpan);
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var start = segment * segmentSize;
            var end = segment == segmentCount - 1 ? totalBytes - 1 : ((segment + 1) * segmentSize) - 1;
            downloads.Add(DownloadSegmentAsync(start, end, temporaryPath, counter, progress, zeroProgressCancellationTokenSource.Token));
        }

        try
        {
            await Task.WhenAll(downloads);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && counter.ReceivedBytes == 0)
        {
            return false;
        }
        catch
        {
            downloadCancellationTokenSource.Cancel();
            try
            {
                await Task.WhenAll(downloads);
            }
            catch
            {
                // Preserve the first real download failure; remaining segments are intentionally cancelled.
            }

            throw;
        }

        return true;
    }

    private static async Task DownloadSegmentAsync(
        long start,
        long end,
        string temporaryPath,
        DownloadCounter counter,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var request = CreateRangeRequest(start, end);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Ollama 安装包下载分段失败，请重试。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Write,
            1024 * 128,
            useAsync: true);
        destination.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[1024 * 128];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            counter.Add(count, progress);
        }
    }

    private static async Task DownloadSingleStreamAsync(string temporaryPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(OfficialInstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Ollama 安装包暂时无法下载，请稍后重试。");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long receivedBytes = 0;
        long reportedPercent = -1;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            receivedBytes += count;
            if (totalBytes is > 0)
            {
                var percent = Math.Min(100, receivedBytes * 100 / totalBytes.Value);
                if (percent != reportedPercent)
                {
                    reportedPercent = percent;
                    ReportProgress(progress, $"{percent}%");
                }
            }
            else
            {
                ReportProgress(progress, null);
            }
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static HttpRequestMessage CreateRangeRequest(long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, OfficialInstallerUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Range = new RangeHeaderValue(start, end);
        return request;
    }

    private static string ResolveInstallerDirectory()
    {
        const string preferredDirectory = @"D:\OliviaLetterOverlay\downloads";
        if (Directory.Exists(Path.GetPathRoot(preferredDirectory)))
        {
            try
            {
                Directory.CreateDirectory(preferredDirectory);
                return preferredDirectory;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OliviaLetterOverlay",
            "installers");
    }

    private static void ReportProgress(IProgress<string>? progress, string? percentText)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(percentText is null ? "正在下载 Ollama…" : $"正在多线程下载 Ollama · {percentText}");
    }

    private sealed class DownloadCounter
    {
        private readonly long _totalBytes;
        private readonly object _progressGate = new();
        private long _receivedBytes;
        private long _reportedPercent;

        public long ReceivedBytes => Volatile.Read(ref _receivedBytes);

        public DownloadCounter(long totalBytes) => _totalBytes = totalBytes;

        public void Add(int count, IProgress<string>? progress)
        {
            if (count <= 0)
            {
                return;
            }

            bool shouldReport;
            string percentText;
            lock (_progressGate)
            {
                _receivedBytes += count;
                var percent = Math.Min(100, (int)(_receivedBytes * 100 / _totalBytes));
                shouldReport = percent != _reportedPercent;
                _reportedPercent = percent;
                percentText = $"{percent}%";
            }

            if (shouldReport)
            {
                ReportProgress(progress, percentText);
            }
        }
    }
}
