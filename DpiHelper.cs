using System.Runtime.InteropServices;

namespace OliviaLetterOverlay;

internal readonly record struct MonitorMetrics(
    IntPtr Handle,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int WorkLeft,
    int WorkTop,
    int WorkRight,
    int WorkBottom,
    uint DpiX,
    uint DpiY)
{
    public int Width => Math.Max(1, Right - Left);
    public int Height => Math.Max(1, Bottom - Top);
    public double ScaleX => DpiX / 96d;
    public double ScaleY => DpiY / 96d;
}

internal static class DpiHelper
{
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;

    public static int DipToPixelsX(double dip, uint dpi) => (int)Math.Round(dip * Math.Max(1, dpi) / 96d);
    public static int DipToPixelsY(double dip, uint dpi) => (int)Math.Round(dip * Math.Max(1, dpi) / 96d);
    public static double PixelsToDipX(int pixels, uint dpi) => pixels * 96d / Math.Max(1, dpi);
    public static double PixelsToDipY(int pixels, uint dpi) => pixels * 96d / Math.Max(1, dpi);

    public static uint GetWindowDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 96;
        }

        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96;
        }
    }

    public static uint GetMonitorDpi(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return 96;
        }

        try
        {
            if (GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch
        {
            // GetDpiForMonitor is unavailable on some older Windows builds;
            // keep the safe 96-DPI fallback for those systems.
        }

        return 96;
    }

    public static MonitorMetrics GetPrimaryMonitor()
    {
        var monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        return GetMonitorMetrics(monitor);
    }

    public static MonitorMetrics GetMonitorForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return GetMonitorMetrics(monitor);
    }

    public static MonitorMetrics GetMonitorMetrics(IntPtr monitor)
    {
        var info = new MonitorInfo();
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new MonitorMetrics(IntPtr.Zero, 0, 0, 1, 1, 0, 0, 1, 1, 96, 96);
        }

        var dpi = GetMonitorDpi(monitor);
        return new MonitorMetrics(
            monitor,
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom,
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom,
            dpi,
            dpi);
    }

    public static IReadOnlyList<MonitorMetrics> EnumerateMonitors()
    {
        var monitors = new List<MonitorMetrics>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            monitors.Add(GetMonitorMetrics(monitor));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        public MonitorInfo()
        {
            Size = Marshal.SizeOf<MonitorInfo>();
            Monitor = default;
            Work = default;
            Flags = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
