using System;
using System.Diagnostics;
using System.IO;

// OliviaLetterOverlay 启动器包装（模仿 linli-local-mail 的 launcher-wrapper 思路）：
// 安装脚本会把游戏的 launcher.exe 改名为 launcher.origin.exe，再把本程序编译为 launcher.exe。
// 玩家照常启动游戏时：先拉起信箱（伴随模式），再把原启动器原样启动，参数原样转发。
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            StartOverlay();
        }
        catch
        {
            // 信箱拉起失败不应影响游戏本身。
        }

        var here = AppDomain.CurrentDomain.BaseDirectory;
        var origin = Path.Combine(here, "launcher.origin.exe");
        var target = File.Exists(origin) ? origin : Path.Combine(here, "Olivia.exe");
        if (!File.Exists(target))
        {
            return 1;
        }

        var quoted = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            quoted[i] = args[i].Contains(" ") ? "\"" + args[i] + "\"" : args[i];
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            Arguments = string.Join(" ", quoted),
            UseShellExecute = true,
            WorkingDirectory = here,
        });
        return 0;
    }

    private static void StartOverlay()
    {
        var overlay = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "OliviaLetterOverlay", "OliviaLetterOverlay.exe");
        if (!File.Exists(overlay))
        {
            return;
        }

        var running = Process.GetProcessesByName("OliviaLetterOverlay");
        if (running != null && running.Length > 0)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = overlay,
            Arguments = "--watch",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(overlay) ?? string.Empty,
        });
    }
}
