using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal sealed record TtsProgress(string Message);

internal sealed class VramInsufficientException : InvalidOperationException
{
    public VramInsufficientException(string message) : base(message)
    {
    }
}

internal sealed class IndexTtsClient
{
    public const int DefaultSeed = 20260830;
    private const int StagedTimeoutMinutes = 10;
    private const int CpuTimeoutMinutes = 20;
    private static readonly string WorkDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "tts-work");

    public static bool IsReady(TtsPreferences preferences)
    {
        try
        {
            var root = preferences.IndexTtsRoot?.Trim();
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var pythonPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
            var workerScript = Path.Combine(root, "local_tools", "olivia_tts_worker.py");
            var modelConfig = Path.Combine(root, "checkpoints", "config.yaml");
            var referencePath = string.IsNullOrWhiteSpace(preferences.ReferencePath)
                ? Path.Combine(root, "reference", "lv_0_reference_6.8-22.1.wav")
                : preferences.ReferencePath.Trim();
            return File.Exists(pythonPath)
                && File.Exists(workerScript)
                && File.Exists(modelConfig)
                && File.Exists(referencePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? FindInstalledRoot(string currentRoot)
    {
        var candidates = new[]
        {
            currentRoot,
            Path.Combine(AppContext.BaseDirectory, "IndexTTS-2.5"),
            @"D:\codex work\IndexTTS-2.5",
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsReady(new TtsPreferences { IndexTtsRoot = candidate }))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<string> GenerateAsync(
        string characterId,
        string? recordKey,
        string reply,
        IProgress<TtsProgress> progress,
        CancellationToken cancellationToken,
        bool forceCpu = false,
        int? seedOverride = null,
        bool regenerate = false)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new InvalidOperationException("当前没有可朗读的回信内容。");
        }

        var preferences = TtsPreferencesStore.Load();
        var pythonPath = Path.Combine(preferences.IndexTtsRoot, ".venv", "Scripts", "python.exe");
        var workerScript = Path.Combine(preferences.IndexTtsRoot, "local_tools", "olivia_tts_worker.py");
        var referencePath = string.IsNullOrWhiteSpace(preferences.ReferencePath)
            ? Path.Combine(preferences.IndexTtsRoot, "reference", "lv_0_reference_6.8-22.1.wav")
            : preferences.ReferencePath;
        if (!File.Exists(pythonPath))
        {
            throw new InvalidOperationException("未找到 IndexTTS-2.5 的 Python 解释器。请确认设置里的引擎目录（需要 .venv\\Scripts\\python.exe）。");
        }

        if (!File.Exists(workerScript))
        {
            throw new InvalidOperationException("未找到 IndexTTS-2.5 的朗读组件。请确认设置里的引擎目录（需要 local_tools\\olivia_tts_worker.py）。");
        }

        if (!File.Exists(referencePath))
        {
            throw new InvalidOperationException("未找到参考音色文件。请确认设置里的音色路径，或清空它以使用引擎自带音色。");
        }

        var seed = seedOverride ?? (preferences.Seed > 0 ? preferences.Seed : DefaultSeed);
        var intervalSilenceMs = preferences.IntervalSilenceMs >= 0 ? preferences.IntervalSilenceMs : 200;
        var maxTextTokens = preferences.MaxTextTokensPerSegment >= 1 ? preferences.MaxTextTokensPerSegment : 120;
        var durationFactor = preferences.DurationFactor is > 0.1 and <= 4.0 ? preferences.DurationFactor : 1.0;
        var fingerprint = SettingsFingerprint(referencePath, intervalSilenceMs, maxTextTokens, durationFactor);

        var cachePath = CachePath(characterId, recordKey, reply, fingerprint);
        if (!regenerate && File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            DiagnosticLog.Write("tts", $"cache_hit key={CacheFileName(recordKey, reply, fingerprint)}");
            return cachePath;
        }

        // 不做显存预检拦截：直接尝试显卡生成，显存不足由 worker 失败（退出码 42）后再报。
        var (freeVramMiB, totalVramMiB) = ReadVramMiB();
        DiagnosticLog.Write("tts", $"preflight free_vram_mib={freeVramMiB?.ToString() ?? "unknown"} total_vram_mib={totalVramMiB?.ToString() ?? "unknown"} force_cpu={forceCpu}");

        Directory.CreateDirectory(WorkDirectory);
        var workDirectory = Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var startedAt = DateTime.Now;
        try
        {
            var speechText = StripSignature(reply);
            if (string.IsNullOrWhiteSpace(speechText))
            {
                throw new InvalidOperationException("这封回信只有署名，没有可朗读的内容。");
            }

            var textFile = Path.Combine(workDirectory, "letter.txt");
            var outputWav = Path.Combine(workDirectory, "letter.wav");
            var reportFile = Path.Combine(workDirectory, "report.json");
            await File.WriteAllTextAsync(textFile, speechText, new UTF8Encoding(false), cancellationToken);

            var (fileName, arguments) = BuildWorkerCommand(
                pythonPath, workerScript, textFile, outputWav, reportFile, referencePath, seed,
                forceCpu ? "cpu" : "staged", intervalSilenceMs, maxTextTokens, durationFactor);
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            DiagnosticLog.Write("tts", $"spawn characters={speechText.Length} seed={seed} interval_ms={intervalSilenceMs} tokens={maxTextTokens} duration={durationFactor.ToString(CultureInfo.InvariantCulture)} regenerate={regenerate}");
            progress.Report(new TtsProgress("加载模型…"));
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = Task.Run(
                async () =>
                {
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                        {
                            progress.Report(new TtsProgress(StageLabel(line["PROGRESS ".Length..].Trim())));
                        }
                    }
                },
                CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var timeoutMinutes = forceCpu ? CpuTimeoutMinutes : StagedTimeoutMinutes;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new InvalidOperationException($"朗读生成超过 {timeoutMinutes} 分钟仍未完成，已中止。");
            }

            await outputTask;
            var error = await errorTask;
            DiagnosticLog.Write("tts", $"exit_code={process.ExitCode} elapsed_s={(DateTime.Now - startedAt).TotalSeconds:F1}");
            TryLogReport(reportFile);
            if (process.ExitCode == 42)
            {
                throw new VramInsufficientException("生成过程中显卡显存不足。游戏或壁纸等程序可能正在占用显卡。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("生成朗读语音失败：" + SummarizeError(error));
            }

            if (!File.Exists(outputWav) || new FileInfo(outputWav).Length == 0)
            {
                throw new InvalidOperationException("生成朗读语音失败：没有生成音频文件。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.Move(outputWav, cachePath, overwrite: true);
            progress.Report(new TtsProgress("朗读中"));
            return cachePath;
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉只影响磁盘占用，不影响朗读结果。
            }
        }
    }

    public static (int? FreeMiB, int? TotalMiB) ReadVramMiB()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                ArgumentList = { "--query-gpu=memory.free,memory.total", "--format=csv,noheader,nounits" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return (null, null);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var firstLine = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            if (firstLine is null)
            {
                return (null, null);
            }

            var values = firstLine.Split(',').Select(part => part.Trim()).ToArray();
            var freeMiB = int.TryParse(values.ElementAtOrDefault(0), out var free) ? free : (int?)null;
            var totalMiB = int.TryParse(values.ElementAtOrDefault(1), out var total) ? total : (int?)null;
            return (freeMiB, totalMiB);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, null);
        }
    }

    public static string CacheFileName(string? recordKey, string reply, string fingerprint)
    {
        string baseName;
        if (!string.IsNullOrWhiteSpace(recordKey))
        {
            var safeKey = new string(recordKey.Trim().Where(char.IsLetterOrDigit).ToArray());
            baseName = safeKey.Length > 0 ? safeKey.ToLowerInvariant() : AdhocName(reply);
        }
        else
        {
            baseName = AdhocName(reply);
        }

        return fingerprint.Length == 0 ? baseName : baseName + "-" + fingerprint;
    }

    private static string AdhocName(string reply)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(reply.Trim()));
        return "adhoc-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string StripSignature(string reply)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n');
        var end = lines.Length;
        while (end > 0)
        {
            var last = lines[end - 1].Trim();
            if (last.Length == 0 || last.StartsWith('—') || last.StartsWith("--", StringComparison.Ordinal))
            {
                end--;
                continue;
            }

            break;
        }

        return string.Join("\n", lines.Take(end)).TrimEnd();
    }

    private static string SettingsFingerprint(string referencePath, int intervalSilenceMs, int maxTextTokens, double durationFactor)
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{referencePath.ToLowerInvariant()}|{intervalSilenceMs}|{maxTextTokens}|{durationFactor.ToString(CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(fingerprint)[..8].ToLowerInvariant();
    }

    public static (string FileName, IReadOnlyList<string> Arguments) BuildWorkerCommand(
        string pythonPath,
        string workerScript,
        string textFile,
        string outputWav,
        string reportFile,
        string referencePath,
        int seed,
        string mode,
        int intervalSilenceMs,
        int maxTextTokens,
        double durationFactor,
        bool dryRun = false)
    {
        var arguments = new List<string>
        {
            workerScript,
            "--text-file", textFile,
            "--output", outputWav,
            "--reference", referencePath,
            "--seed", seed.ToString(CultureInfo.InvariantCulture),
            "--mode", mode,
            "--interval-silence", intervalSilenceMs.ToString(CultureInfo.InvariantCulture),
            "--max-text-tokens", maxTextTokens.ToString(CultureInfo.InvariantCulture),
            "--duration-factor", durationFactor.ToString(CultureInfo.InvariantCulture),
            "--report", reportFile,
        };
        if (dryRun)
        {
            arguments.Add("--dry-run");
        }

        return (pythonPath, arguments);
    }

    private static string CachePath(string characterId, string? recordKey, string reply, string fingerprint) => Path.Combine(
        CharacterStore.GetDataDirectory(characterId),
        "tts",
        CacheFileName(recordKey, reply, fingerprint) + ".wav");

    private static string StageLabel(string stage) => stage switch
    {
        "loading-models" => "加载模型…",
        "generating" => "生成中…",
        "validating" => "校验音频…",
        _ => "生成中…",
    };

    private static string SummarizeError(string error)
    {
        var lastLine = error.Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0) ?? "没有错误详情。";
        return DiagnosticLog.Redact(lastLine.Length > 200 ? lastLine[..200] + "…" : lastLine);
    }

    private static void TryLogReport(string reportFile)
    {
        try
        {
            if (!File.Exists(reportFile))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(reportFile));
            var root = document.RootElement;
            var initSeconds = root.TryGetProperty("initialization_seconds", out var init) ? init.GetDouble() : -1;
            var generationSeconds = root.TryGetProperty("generation_seconds", out var generation) ? generation.GetDouble() : -1;
            var melTruncated = root.TryGetProperty("max_mel_truncated", out var mel) && mel.GetBoolean();
            DiagnosticLog.Write("tts", $"report init_s={initSeconds:F1} generate_s={generationSeconds:F1} mel_truncated={melTruncated}");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // report 只用于诊断，解析失败不影响朗读结果。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已经退出时无需清理。
        }
    }
}
