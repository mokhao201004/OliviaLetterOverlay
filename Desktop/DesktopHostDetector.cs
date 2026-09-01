using System.Text;

namespace OliviaLetterOverlay;

internal sealed record DesktopWindowNode(
    IntPtr Handle,
    string ClassName,
    string WindowText,
    IntPtr Parent,
    uint ProcessId,
    Win32Native.Rect Rect,
    bool Visible,
    int ZOrder,
    bool TopLevel);

internal sealed record DesktopHostSnapshot(
    IntPtr Progman,
    uint ExplorerProcessId,
    IntPtr IconHost,
    IntPtr IconView,
    IntPtr IconList,
    IntPtr WallpaperHost,
    IReadOnlyList<DesktopWindowNode> Windows);

/// <summary>
/// Enumerates the live Explorer desktop window tree. This is intentionally a
/// read-only detector; attaching or rendering belongs to a separate manager.
/// </summary>
internal sealed class DesktopHostDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
        "SysListView32",
    };

    public DesktopHostSnapshot Capture(bool refreshShell = false)
    {
        var progman = Win32Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            throw new InvalidOperationException("没有找到 Progman 桌面窗口。");
        }

        var explorerProcessId = Win32Native.ReadProcessId(progman);
        if (refreshShell)
        {
            // This is an undocumented Explorer convention used by live
            // wallpapers to materialize a WorkerW sibling. Keep it isolated
            // here so callers can choose whether to request it.
            _ = Win32Native.SendMessageTimeout(
                progman,
                Win32Native.WmProgman,
                new IntPtr(0xD),
                new IntPtr(1),
                Win32Native.SmtoNormal,
                1000,
                out _);
        }

        var windows = new List<DesktopWindowNode>();
        var seen = new HashSet<IntPtr>();
        var zOrder = 0;

        Win32Native.EnumWindows((handle, _) =>
        {
            AddWindow(handle, IntPtr.Zero, true, zOrder++);
            var className = Win32Native.ReadClassName(handle);
            if (className is "Progman" or "WorkerW")
            {
                Win32Native.EnumChildWindows(handle, (child, _) =>
                {
                    AddWindow(child, Win32Native.GetParent(child), false, zOrder++);
                    return true;
                }, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);

        var shellWindows = windows.Where(window => window.ProcessId == explorerProcessId).ToArray();
        var iconView = shellWindows.FirstOrDefault(window => window.ClassName == "SHELLDLL_DefView");
        var iconHost = iconView?.Parent ?? progman;
        var iconList = iconView is null
            ? IntPtr.Zero
            : shellWindows.FirstOrDefault(window => window.ClassName == "SysListView32" && window.Parent == iconView.Handle)?.Handle ?? IntPtr.Zero;

        var workerWindows = shellWindows
            .Where(window => window.ClassName == "WorkerW"
                && !shellWindows.Any(child => child.Parent == window.Handle && child.ClassName == "SHELLDLL_DefView"))
            .OrderBy(window => window.ZOrder)
            .ToArray();
        var wallpaperHost = FindWallpaperHost(workerWindows, iconHost, progman);

        return new DesktopHostSnapshot(
            progman,
            explorerProcessId,
            iconHost,
            iconView?.Handle ?? IntPtr.Zero,
            iconList,
            wallpaperHost,
            windows);

        void AddWindow(IntPtr handle, IntPtr parent, bool topLevel, int order)
        {
            if (handle == IntPtr.Zero || !seen.Add(handle))
            {
                return;
            }

            var className = Win32Native.ReadClassName(handle);
            if (!topLevel && !ShellClasses.Contains(className))
            {
                return;
            }

            windows.Add(new DesktopWindowNode(
                handle,
                className,
                Win32Native.ReadWindowText(handle),
                parent,
                Win32Native.ReadProcessId(handle),
                Win32Native.ReadRect(handle),
                Win32Native.IsWindowVisible(handle),
                order,
                topLevel));
        }
    }

    public void Log(DesktopHostSnapshot snapshot)
    {
        DiagnosticLog.Write("desktop.detector", $"Found Progman: {Format(snapshot.Progman)} explorer_pid={snapshot.ExplorerProcessId}");
        DiagnosticLog.Write("desktop.detector", $"Found SHELLDLL_DefView: {Format(snapshot.IconView)} icon_host={Format(snapshot.IconHost)}");
        DiagnosticLog.Write("desktop.detector", $"Found SysListView32: {Format(snapshot.IconList)}");
        DiagnosticLog.Write("desktop.detector", $"Selected WorkerW: {Format(snapshot.WallpaperHost)}");

        foreach (var window in snapshot.Windows.Where(window => ShellClasses.Contains(window.ClassName)).OrderBy(window => window.ZOrder))
        {
            DiagnosticLog.Write("desktop.tree", Format(window));
        }
    }

    public static string Format(DesktopWindowNode? window)
    {
        return window is null
            ? "0x0"
            : $"HWND={Format(window.Handle)} Class={window.ClassName} Parent={Format(window.Parent)} PID={window.ProcessId} Rect={window.Rect} Visible={window.Visible} TopLevel={window.TopLevel}";
    }

    public static string Format(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static IntPtr FindWallpaperHost(
        IReadOnlyList<DesktopWindowNode> workers,
        IntPtr iconHost,
        IntPtr progman)
    {
        if (workers.Count == 0)
        {
            return progman;
        }

        var visible = workers.Where(window => window.Visible && window.Handle != iconHost).ToArray();
        if (visible.Length == 0)
        {
            return workers[0].Handle;
        }

        // Explorer can expose several stale/top-level WorkerW windows. The
        // live wallpaper slot is the visible WorkerW directly owned by
        // Progman, preferably the one covering the desktop bounds.
        return visible
            .OrderByDescending(window => window.Parent == progman)
            .ThenByDescending(window => (long)(window.Rect.Right - window.Rect.Left) * (window.Rect.Bottom - window.Rect.Top))
            .ThenBy(window => window.ZOrder)
            .First()
            .Handle;
    }
}
