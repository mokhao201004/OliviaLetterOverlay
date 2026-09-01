using System.Runtime.InteropServices;

internal sealed class UpdaterProgress : IDisposable
{
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const int SwShow = 5;
    private const uint WsExAppWindow = 0x00040000;
    private const uint WsOverlapped = 0x00000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsTabStop = 0x00010000;
    private const uint WsGroup = 0x00020000;
    private const uint SsLeft = 0x00000000;
    private const uint PbsSmooth = 0x01;
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmSetFont = 0x0030;
    private const uint WmUser = 0x0400;
    private const uint WmProgressUpdate = WmUser + 1;
    private const uint WmProgressClose = WmUser + 2;
    private const uint PbmSetRange32 = WmUser + 6;
    private const uint PbmSetPos = WmUser + 2;
    private const uint PbmSetMarquee = WmUser + 10;
    private const uint WmSetRedraw = 0x000B;
    private const uint WmGetFont = 0x0031;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private readonly string _className = "OliviaLetterUpdaterProgress_" + Environment.ProcessId;
    private readonly WndProcDelegate _wndProc;
    private IntPtr _window;
    private IntPtr _stageLabel;
    private IntPtr _detailLabel;
    private IntPtr _progressBar;
    private IntPtr _font;
    private bool _disposed;

    public UpdaterProgress()
    {
        _wndProc = WindowProc;
    }

    public void Create()
    {
        if (_window != IntPtr.Zero)
        {
            return;
        }

        InitCommonControls();
        var instance = GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hCursor = LoadCursor(IntPtr.Zero, 32512),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            lpszClassName = _className,
        };
        RegisterClassEx(ref windowClass);

        _window = CreateWindowEx(
            WsExAppWindow,
            _className,
            "Olivia Letter 信箱升级",
            WsOverlapped | WsCaption | WsSysMenu | WsMinimizeBox,
            CwUseDefault,
            CwUseDefault,
            520,
            190,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法创建更新进度窗口。错误码：" + Marshal.GetLastWin32Error());
        }

        _font = CreateFont(
            18,
            0,
            0,
            0,
            400,
            0,
            0,
            0,
            134,
            0,
            0,
            5,
            0,
            "Microsoft YaHei UI");

        _stageLabel = CreateWindowEx(
            0,
            "STATIC",
            "正在准备更新…",
            WsChild | WsVisible | SsLeft,
            24,
            20,
            465,
            28,
            _window,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        _detailLabel = CreateWindowEx(
            0,
            "STATIC",
            "请稍候，更新器正在工作。",
            WsChild | WsVisible | SsLeft,
            24,
            52,
            465,
            26,
            _window,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        _progressBar = CreateWindowEx(
            0,
            "msctls_progress32",
            string.Empty,
            WsChild | WsVisible | PbsSmooth,
            24,
            96,
            465,
            24,
            _window,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        SendMessage(_stageLabel, WmSetFont, _font, new IntPtr(1));
        SendMessage(_detailLabel, WmSetFont, _font, new IntPtr(1));
        SendMessage(_progressBar, PbmSetRange32, IntPtr.Zero, MakeLParam(0, 100));
        SendMessage(_progressBar, PbmSetPos, new IntPtr(0), IntPtr.Zero);
        CenterWindow(_window, 520, 190);
        ShowWindow(_window, SwShow);
        UpdateWindow(_window);
    }

    public void RunMessageLoop()
    {
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Report(string stage, string detail, int value, int max = 100, bool marquee = false)
    {
        if (_window == IntPtr.Zero || !IsWindow(_window))
        {
            return;
        }

        var update = new ProgressUpdate(stage, detail, value, max, marquee);
        var handle = GCHandle.Alloc(update);
        try
        {
            SendMessage(_window, WmProgressUpdate, IntPtr.Zero, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    public void Close()
    {
        if (_window != IntPtr.Zero && IsWindow(_window))
        {
            PostMessage(_window, WmProgressClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        if (_font != IntPtr.Zero)
        {
            DeleteObject(_font);
            _font = IntPtr.Zero;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmProgressUpdate:
            {
                var handle = GCHandle.FromIntPtr(lParam);
                if (handle.Target is ProgressUpdate update)
                {
                    SetWindowText(_stageLabel, update.Stage);
                    SetWindowText(_detailLabel, update.Detail);
                    SendMessage(_progressBar, PbmSetMarquee, IntPtr.Zero, IntPtr.Zero);
                    var maximum = Math.Max(update.Maximum, 1);
                    var value = update.Marquee ? 0 : Math.Clamp(update.Value, 0, maximum);
                    SendMessage(_progressBar, PbmSetRange32, IntPtr.Zero, MakeLParam(0, maximum));
                    SendMessage(_progressBar, PbmSetPos, new IntPtr(value), IntPtr.Zero);
                }

                return IntPtr.Zero;
            }
            case WmProgressClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private static void CenterWindow(IntPtr hwnd, int width, int height)
    {
        var x = Math.Max(0, (GetSystemMetrics(SmCxScreen) - width) / 2);
        var y = Math.Max(0, (GetSystemMetrics(SmCyScreen) - height) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004);
    }

    private static IntPtr MakeLParam(int low, int high)
    {
        return new IntPtr((high << 16) | (low & 0xFFFF));
    }

    private sealed record ProgressUpdate(string Stage, string Detail, int Value, int Maximum, bool Marquee);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    private const int COLOR_WINDOW = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowText(IntPtr hwnd, string text);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("comctl32.dll")]
    private static extern void InitCommonControls();
}
