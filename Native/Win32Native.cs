using System.Runtime.InteropServices;
using System.Text;

namespace OliviaLetterOverlay;

internal static class Win32Native
{
    internal const uint GwHwndNext = 2;
    internal const int WmProgman = 0x052C;
    internal const uint SmtoNormal = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public override readonly string ToString() => $"{Left},{Top}-{Right},{Bottom}";
    }

    internal delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    internal static string ReadClassName(IntPtr handle)
    {
        var value = new StringBuilder(128);
        return GetClassName(handle, value, value.Capacity) > 0 ? value.ToString() : string.Empty;
    }

    internal static string ReadWindowText(IntPtr handle)
    {
        var value = new StringBuilder(256);
        return GetWindowText(handle, value, value.Capacity) > 0 ? value.ToString() : string.Empty;
    }

    internal static uint ReadProcessId(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    internal static Rect ReadRect(IntPtr handle)
    {
        return GetWindowRect(handle, out var rect) ? rect : default;
    }
}
