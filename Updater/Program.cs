using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconError = 0x00000010;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "OliviaLetterUpdater.log");
    private static readonly string[] UpdateMirrors =
    {
        "https://github.com/sron0404/OliviaLetterOverlay/releases/download/v1.3/",
        "https://gitee.com/sron0404/OliviaLetterOverlay/releases/download/v1.3/",
    };
    private static readonly string[] UpgradeParts =
    {
        "OliviaLetterOverlay-1.3-upgrade-win-x64.zip.001",
        "OliviaLetterOverlay-1.3-upgrade-win-x64.zip.002",
        "OliviaLetterOverlay-1.3-upgrade-win-x64.zip.003",
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            using var progress = new UpdaterProgress();
            progress.Create();

            string? installRoot = null;
            Exception? failure = null;
            var worker = Task.Run(() =>
            {
                try
                {
                    installRoot = Run(args, progress);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    Log("ERROR " + ex);
                }
                finally
                {
                    progress.Close();
                }
            });

            progress.RunMessageLoop();
            worker.GetAwaiter().GetResult();
            if (failure is not null)
            {
                throw failure;
            }

            ShowMessage(
                "升级完成。\n\n程序位置：" + installRoot +
                "\n\n角色、信件、记忆、API Key 和音乐设置已保留。",
                "Olivia Letter 信箱升级",
                MbIconInformation);
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex);
            ShowMessage(
                "Olivia Letter 信箱升级失败。\n\n" + ex.Message +
                "\n\n详细日志：" + LogPath,
                "Olivia Letter 信箱升级",
                MbIconError);
            return 1;
        }
    }

    private static string Run(string[] args, UpdaterProgress progress)
    {
        var packageRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var payloadRoot = Path.Combine(packageRoot, "installer", "payload");
        string? downloadRoot = null;
        progress.Report("正在检查升级包", "正在检查本地升级文件…", 0);
        if (!File.Exists(Path.Combine(payloadRoot, "OliviaLetterOverlay.exe")))
        {
            payloadRoot = packageRoot;
        }

        var sourceExe = Path.Combine(payloadRoot, "OliviaLetterOverlay.exe");
        if (!File.Exists(sourceExe))
        {
            progress.Report("正在下载升级包", "本地未找到升级文件，正在连接发布服务器…", 0);
            (payloadRoot, downloadRoot) = DownloadAndExtractPackage(progress);
            sourceExe = Path.Combine(payloadRoot, "OliviaLetterOverlay.exe");
        }
        else
        {
            progress.Report("正在准备升级", "已找到本地升级文件，准备安装…", 12);
        }

        Log($"START package={packageRoot} source={payloadRoot}");
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "OliviaLetterOverlay");
        var stageRoot = Path.Combine(Path.GetTempPath(), "OliviaLetterOverlay-update-" + Guid.NewGuid().ToString("N"));

        try
        {
            progress.Report("正在准备升级", "正在校验并准备文件…", 20);
            Directory.CreateDirectory(stageRoot);
            CopyDirectory(payloadRoot, stageRoot);
            var stagedExe = Path.Combine(stageRoot, "OliviaLetterOverlay.exe");
            if (!File.Exists(stagedExe))
            {
                throw new InvalidDataException("升级包内的程序文件复制不完整。请重新解压后再试。\n来源：" + sourceExe);
            }

            Log("STAGE_READY " + stagedExe);
            progress.Report("正在安装升级", "正在停止旧版本并复制新文件…", 75);
            StopRunningApplication();
            Directory.CreateDirectory(installRoot);
            CopyDirectory(stageRoot, installRoot);
            Log("COPIED install=" + installRoot);
            progress.Report("正在安装升级", "正在更新快捷方式…", 92);
            CreateShortcuts(installRoot);

            var noLaunch = args.Any(arg => string.Equals(arg, "--no-launch", StringComparison.OrdinalIgnoreCase));
            if (!noLaunch)
            {
                var installedExe = Path.Combine(installRoot, "OliviaLetterOverlay.exe");
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    WorkingDirectory = installRoot,
                    UseShellExecute = true,
                });
                Log("LAUNCHED " + installedExe);
            }

            progress.Report("升级完成", "文件已更新，正在启动 Olivia Letter…", 100);
            return installRoot;
        }
        finally
        {
            TryDeleteDirectory(stageRoot);
            if (downloadRoot is not null)
            {
                TryDeleteDirectory(downloadRoot);
            }
        }
    }

    private static (string PayloadRoot, string DownloadRoot) DownloadAndExtractPackage(UpdaterProgress progress)
    {
        var downloadRoot = Path.Combine(Path.GetTempPath(), "OliviaLetterOverlay-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadRoot);
        try
        {
            Log("LOCAL_PAYLOAD_MISSING downloading upgrade package");
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OliviaLetterUpdater/1.3");

            var downloadedParts = new List<string>(UpgradeParts.Length);
            for (var index = 0; index < UpgradeParts.Length; index++)
            {
                var part = UpgradeParts[index];
                var target = Path.Combine(downloadRoot, part);
                DownloadPart(client, part, target, progress, index, UpgradeParts.Length);
                downloadedParts.Add(target);
            }

            var zipPath = Path.Combine(downloadRoot, "OliviaLetterOverlay-1.3-upgrade-win-x64.zip");
            progress.Report("正在整理升级包", "正在合并下载分卷…", 91);
            using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var part in downloadedParts)
                {
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
                    input.CopyTo(output);
                }
            }

            var extractRoot = Path.Combine(downloadRoot, "package");
            progress.Report("正在整理升级包", "正在解压升级文件…", 95);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);
            var payloadRoot = Path.Combine(extractRoot, "installer", "payload");
            if (!File.Exists(Path.Combine(payloadRoot, "OliviaLetterOverlay.exe")))
            {
                payloadRoot = extractRoot;
            }

            if (!File.Exists(Path.Combine(payloadRoot, "OliviaLetterOverlay.exe")))
            {
                throw new InvalidDataException("在线升级包解压后没有找到 OliviaLetterOverlay.exe。请检查发布包内容。\n详细日志：" + LogPath);
            }

            Log("DOWNLOAD_READY payload=" + payloadRoot);
            return (payloadRoot, downloadRoot);
        }
        catch
        {
            TryDeleteDirectory(downloadRoot);
            throw;
        }
    }

    private static void DownloadPart(
        HttpClient client,
        string fileName,
        string target,
        UpdaterProgress progress,
        int partIndex,
        int partCount)
    {
        var errors = new List<string>();
        foreach (var mirror in UpdateMirrors)
        {
            try
            {
                var uri = new Uri(mirror + fileName);
                Log("DOWNLOAD_BEGIN " + uri);
                using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{mirror} -> {(int)response.StatusCode}");
                    continue;
                }

                using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                var totalBytes = response.Content.Headers.ContentLength;
                var copiedBytes = 0L;
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    copiedBytes += read;
                    var partProgress = totalBytes is > 0
                        ? (int)Math.Clamp(copiedBytes * 100L / totalBytes.Value, 0, 100)
                        : 0;
                    var overallProgress = (partIndex * 100 + partProgress) / partCount;
                    var detail = totalBytes is > 0
                        ? $"正在下载第 {partIndex + 1}/{partCount} 个分卷：{FormatBytes(copiedBytes)} / {FormatBytes(totalBytes.Value)}"
                        : $"正在下载第 {partIndex + 1}/{partCount} 个分卷：{FormatBytes(copiedBytes)}";
                    progress.Report("正在下载升级包", detail, overallProgress, marquee: totalBytes is not > 0);
                }
                Log($"DOWNLOAD_END file={fileName} bytes={new FileInfo(target).Length}");
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{mirror} -> {ex.Message}");
                TryDeleteFile(target);
            }
        }

        throw new IOException(
            "无法下载升级包分卷 " + fileName +
            "。请确认网络正常，或下载全部分卷后解压再运行更新器。\n" +
            string.Join("\n", errors));
    }

    private static string FormatBytes(long bytes)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;
        return bytes >= mega
            ? $"{bytes / mega:0.0} MB"
            : $"{bytes / kilo:0} KB";
    }

    private static void StopRunningApplication()
    {
        foreach (var process in Process.GetProcessesByName("OliviaLetterOverlay"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    Log($"STOP pid={process.Id}");
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                    }

                    if (!process.WaitForExit(5000) && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Log($"STOP_FAILED pid={process.Id} error={ex.Message}");
                }
            }
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void CreateShortcuts(string installRoot)
    {
        var installedExe = Path.Combine(installRoot, "OliviaLetterOverlay.exe");
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var programsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(startupDirectory);
        Directory.CreateDirectory(programsDirectory);

        CreateShortcut(
            Path.Combine(programsDirectory, "OliviaLetterOverlay.lnk"),
            installedExe,
            installRoot,
            string.Empty,
            "Olivia Letter 信箱");
        CreateShortcut(
            Path.Combine(startupDirectory, "OliviaLetterOverlay 伴随启动.lnk"),
            installedExe,
            installRoot,
            "--watch",
            "开机静默运行，检测到 Olivia 游戏窗口后自动打开信箱");
    }

    private static void CreateShortcut(string path, string target, string workingDirectory, string arguments, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Log("SHORTCUT_SKIPPED WScript.Shell unavailable");
                return;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            try
            {
                var shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: new object[] { path });
                if (shortcut is null)
                {
                    return;
                }

                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
                ReleaseComObject(shortcut);
            }
            finally
            {
                ReleaseComObject(shell);
            }

            Log("SHORTCUT_CREATED " + path);
        }
        catch (Exception ex)
        {
            Log("SHORTCUT_FAILED path=" + path + " error=" + ex.Message);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log("CLEANUP_FAILED path=" + path + " error=" + ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log("FILE_CLEANUP_FAILED path=" + path + " error=" + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging must never prevent an update from completing.
        }
    }

    private static void ShowMessage(string message, string title, uint icon)
    {
        MessageBox(IntPtr.Zero, message, title, MbOk | icon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
