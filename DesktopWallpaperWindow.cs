using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Threading;

namespace OliviaLetterOverlay;

public sealed record WallpaperPlaybackState(TimeSpan Position, TimeSpan Duration, bool IsPlaying, bool IsLooping, bool IsMuted);

// 原生 WMP 视频表面能在桌面子窗口内渲染；WPF MediaElement 在相同位置只保留声音而没有画面。
public sealed class DesktopWallpaperWindow : IDisposable
{
    private static readonly IntPtr HwndBottom = new(1);
    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private readonly Form _form = new()
    {
        FormBorderStyle = FormBorderStyle.None,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        BackColor = System.Drawing.Color.Black,
    };
    private readonly WindowsMediaPlayerHost _player = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _desktopHost;
    private IntPtr _desktopIconView;
    private IntPtr _windowHandle;
    private bool _disposed;
    private bool _isPlaying;

    public DesktopWallpaperWindow()
    {
        _player.Dock = DockStyle.Fill;
        _form.Controls.Add(_player);
        _progressTimer.Tick += (_, _) => RefreshPlaybackState();
    }

    public event EventHandler<WallpaperPlaybackState>? PlaybackStateChanged;

    public bool IsLooping { get; set; }

    internal bool IsAttachedToDesktopHost => _windowHandle != IntPtr.Zero
        && _desktopHost != IntPtr.Zero
        && GetParent(_windowHandle) == _desktopHost;

    internal bool IsBelowDesktopIcons
    {
        get
        {
            var child = _desktopIconView;
            while (child != IntPtr.Zero)
            {
                child = GetWindow(child, 2);
                if (child == _windowHandle)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void PlayWallpaper(string videoPath)
    {
        ThrowIfDisposed();
        EnsureDesktopHost();
        ShowOnDesktop();
        _player.Open(videoPath, IsLooping);
        _isPlaying = true;
        _progressTimer.Start();
        PublishPlaybackState();
    }

    public void TogglePlayback()
    {
        ThrowIfDisposed();
        if (_isPlaying)
        {
            _player.Pause();
            _isPlaying = false;
        }
        else
        {
            ShowOnDesktop();
            _player.Play();
            _isPlaying = true;
            _progressTimer.Start();
        }

        PublishPlaybackState();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        _player.SetPosition(Math.Clamp(position.TotalSeconds, 0, _player.DurationSeconds));
        PublishPlaybackState();
    }

    public void ToggleMuted()
    {
        ThrowIfDisposed();
        _player.SetMuted(!_player.IsMuted);
        PublishPlaybackState();
    }

    public void ToggleLooping()
    {
        ThrowIfDisposed();
        IsLooping = !IsLooping;
        _player.SetLooping(IsLooping);
        PublishPlaybackState();
    }

    internal void EnsureDesktopHost()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        _desktopHost = FindWindow("Progman", null);
        _desktopIconView = FindWindowEx(_desktopHost, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (_desktopHost == IntPtr.Zero || _desktopIconView == IntPtr.Zero)
        {
            throw new InvalidOperationException("没有找到 Windows 桌面图标层。请先重启资源管理器后再试。");
        }

        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        _form.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        _form.Show();
        _windowHandle = _form.Handle;
        SetParent(_windowHandle, _desktopHost);
        var style = GetWindowLong(_windowHandle, GwlStyle);
        SetWindowLong(_windowHandle, GwlStyle, (style & ~WsPopup) | WsChild | WsVisible);
        SetWindowPos(_desktopIconView, IntPtr.Zero, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        SetWindowPos(_windowHandle, HwndBottom, 0, 0, width, height, SwpNoActivate | SwpShowWindow);
        DiagnosticLog.Write("wallpaper", $"native player child created width={width} height={height} attached={IsAttachedToDesktopHost} below_icons={IsBelowDesktopIcons}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _progressTimer.Stop();
        _player.Dispose();
        _form.Dispose();
    }

    private void RefreshPlaybackState()
    {
        if (_disposed)
        {
            return;
        }

        var duration = _player.DurationSeconds;
        if (_isPlaying && duration > 0 && _player.PlayState is 1 or 8)
        {
            _isPlaying = false;
            _progressTimer.Stop();
            _form.Hide();
        }

        PublishPlaybackState();
    }

    private void ShowOnDesktop()
    {
        if (!_form.Visible)
        {
            _form.Show();
        }

        SetWindowPos(_windowHandle, HwndBottom, 0, 0, _form.Width, _form.Height, SwpNoActivate | SwpShowWindow);
    }

    private void PublishPlaybackState()
    {
        if (_disposed)
        {
            return;
        }

        PlaybackStateChanged?.Invoke(this, new WallpaperPlaybackState(
            TimeSpan.FromSeconds(_player.PositionSeconds), TimeSpan.FromSeconds(_player.DurationSeconds),
            _isPlaying, IsLooping, _player.IsMuted));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DesktopWallpaperWindow));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal sealed class WindowsMediaPlayerHost : AxHost
{
    private const string WindowsMediaPlayerClsid = "6BF52A52-394A-11D3-B153-00C04F79FAA6";

    public WindowsMediaPlayerHost() : base(WindowsMediaPlayerClsid)
    {
    }

    public void Open(string path, bool loop)
    {
        CreateControl();
        dynamic player = Player;
        player.uiMode = "none";
        player.settings.setMode("loop", loop);
        player.URL = path;
        player.controls.play();
    }

    public double PositionSeconds
    {
        get
        {
            try { return Math.Max(0, Convert.ToDouble(Player.controls.currentPosition)); }
            catch (COMException) { return 0; }
        }
    }

    public void SetPosition(double value)
    {
        try { Player.controls.currentPosition = value; }
        catch (COMException) { }
    }

    public double DurationSeconds
    {
        get
        {
            try { return Math.Max(0, Convert.ToDouble(Player.currentMedia.duration)); }
            catch (COMException) { return 0; }
        }
    }

    public int PlayState
    {
        get
        {
            try { return Convert.ToInt32(Player.playState); }
            catch (COMException) { return 0; }
        }
    }

    public bool IsMuted
    {
        get
        {
            try { return Convert.ToBoolean(Player.settings.mute); }
            catch (COMException) { return false; }
        }
    }

    public void SetMuted(bool value)
    {
        try { Player.settings.mute = value; }
        catch (COMException) { }
    }

    public void SetLooping(bool value)
    {
        try { Player.settings.setMode("loop", value); }
        catch (COMException) { }
    }

    public void Pause() => Player.controls.pause();

    public void Play() => Player.controls.play();

    private dynamic Player => GetOcx() ?? throw new InvalidOperationException("Windows Media Player 控件尚未初始化。");
}
