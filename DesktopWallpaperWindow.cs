using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using OliviaLetterOverlay.Rendering;
using OliviaLetterOverlay.Video;

namespace OliviaLetterOverlay;

public sealed record WallpaperPlaybackState(TimeSpan Position, TimeSpan Duration, bool IsPlaying, bool IsLooping, bool IsMuted);

// Media Foundation 解码 + D3D11 渲染，视频不会再创建独立的 EVR 全屏窗口。
public sealed class DesktopWallpaperWindow : IDisposable
{
    private static readonly IntPtr HwndBottom = new(1);
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const uint LwaAlpha = 0x00000002;
    private const int WmProgman = 0x052C;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint SmtoNormal = 0x0000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int SwShownoactivate = 4;
    private const int SwHide = 0;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrev = 3;
    private const uint GwOwner = 4;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint CsHRedraw = 0x0002;
    private const uint CsVRedraw = 0x0001;
    private const int IddcArrow = 32512;

    private static readonly WindowProc RenderWindowProc = RenderWndProc;
    private static readonly string RenderWindowClass = $"OliviaWallpaperRender_{Environment.ProcessId}";
    private static ushort _renderWindowClassAtom;
    private static DesktopWallpaperWindow? _activeInstance;
    private readonly MediaFoundationVideoDecoder _decoder = new();
    private readonly D3D11Renderer _renderer = new();
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly WallpaperTransitionController _transition = new();
    private IntPtr _desktopHost;
    private IntPtr _renderParent;
    private IntPtr _desktopIconHost;
    private IntPtr _desktopIconView;
    private uint _desktopShellProcessId;
    private IntPtr _windowHandle;
    private bool _disposed;
    private bool _hasVideo;
    private bool _firstFrameReady;
    private bool _endOfStreamReached;
    private bool _endingTransitionStarted;
    private bool _endingFadeCompleted;
    private bool _endingTransitionCompleted;
    private string? _startupVideoPath;
    private Stopwatch? _startupStopwatch;
    private int _startupRetryCount;
    private long _startupGeneration;
    private DateTime _lastVideoPresentUtc = DateTime.MinValue;
    private DateTime _lowVideoBufferSinceUtc = DateTime.MinValue;
    private DateTime _lastStartupWatchdogUtc = DateTime.MinValue;
    private DateTime _lastSchedulerLogUtc = DateTime.MinValue;
    private DateTime _lastAvSyncLogUtc = DateTime.MinValue;
    private DateTime _lastPerformanceLogUtc = DateTime.MinValue;
    private DateTime _lastCpuSampleUtc = DateTime.MinValue;
    private TimeSpan _lastCpuTime;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private MonitorMetrics _monitorMetrics;
    private IntPtr _monitorHandle;
    private long _renderTickCount;
    private int _frameTimerRestartQueued;
    private const int DesktopFadeOutDurationMilliseconds = 300;
    private const int VideoFadeInDurationMilliseconds = 800;
    private const int PauseVideoFadeOutDurationMilliseconds = 500;
    private const int VideoResumeFadeInDurationMilliseconds = 600;
    private const int StopVideoFadeOutDurationMilliseconds = 500;
    private const int EndVideoFadeOutDurationMilliseconds = 1000;

    public DesktopWallpaperWindow()
    {
        _activeInstance = this;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _progressTimer.Tick += (_, _) => RefreshPlaybackState();
        _frameTimer.Tick += (_, _) => RenderNextFrame();
        _decoder.FrameAvailable += Decoder_FrameAvailable;
        _transition.Updated += (_, _) => SetVideoFadeFactor(_transition.FadeFactor);
    }

    public event EventHandler<WallpaperPlaybackState>? PlaybackStateChanged;

    public bool IsLooping { get; set; }

    internal bool IsAttachedToDesktopHost
    {
        get
        {
            var actualParent = _windowHandle == IntPtr.Zero ? IntPtr.Zero : GetParent(_windowHandle);
            return _windowHandle != IntPtr.Zero && _renderParent != IntPtr.Zero && actualParent == _renderParent;
        }
    }

    internal bool IsBelowDesktopIcons
    {
        get
        {
            if (_desktopHost == IntPtr.Zero || _renderParent == IntPtr.Zero || _desktopIconHost == IntPtr.Zero || _desktopIconView == IntPtr.Zero)
            {
                return false;
            }

            // Windows 11's raised-desktop layout places the icon view and the
            // wallpaper WorkerW under the same Progman parent.  Our layered
            // child must sit between those two Explorer children.
            if (_renderParent == _desktopIconHost && _desktopHost != _desktopIconHost)
            {
                var raisedChild = GetWindow(_desktopIconView, GwHwndNext);
                while (raisedChild != IntPtr.Zero)
                {
                    if (raisedChild == _windowHandle)
                    {
                        return true;
                    }

                    if (raisedChild == _desktopHost)
                    {
                        return false;
                    }

                    raisedChild = GetWindow(raisedChild, GwHwndNext);
                }

                return false;
            }

            // The normal Windows layout uses a dedicated WorkerW behind the
            // icon host. In that layout, child z-order cannot be compared
            // across parents; the dedicated host itself is the proof that the
            // wallpaper is below the icon layer.
            if (_desktopHost != _desktopIconHost)
            {
                var hostParent = GetParent(_desktopHost);
                return IsWindow(_desktopHost) && (hostParent == IntPtr.Zero || hostParent == _desktopIconHost);
            }

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
        var generation = Interlocked.Increment(ref _startupGeneration);
        if (_hasVideo)
        {
            // A new selection owns the only startup generation.  Stop the
            // previous decoder before the new fade callback can open another
            // SourceReader, so stale audio/video work cannot race the switch.
            DiagnosticLog.Write("startup", $"cancel previous media generation={generation - 1}");
            _frameTimer.Stop();
            _progressTimer.Stop();
            _decoder.StopAndRelease();
            _hasVideo = false;
            _firstFrameReady = false;
        }
        if (!_hasVideo && _windowHandle == IntPtr.Zero)
        {
            _transition.SetImmediate(WallpaperTransitionState.Stopped, 1);
        }

        EnsureDesktopHost();
        LogDesktopHierarchy("play-before");
        _transition.TransitionTo(WallpaperTransitionState.WallpaperFadeToBlack, 0,
            TimeSpan.FromMilliseconds(DesktopFadeOutDurationMilliseconds), () => StartVideoUnderBlack(videoPath, generation));

        PublishPlaybackState();
    }

    public void TogglePlayback()
    {
        ThrowIfDisposed();
        switch (_transition.State)
        {
            case WallpaperTransitionState.Playing:
            case WallpaperTransitionState.VideoFadeIn:
            case WallpaperTransitionState.ResumingPrepare:
            case WallpaperTransitionState.Resuming:
            case WallpaperTransitionState.ResumeWallpaperFadeToBlack:
                PauseWallpaper();
                break;
            case WallpaperTransitionState.Pausing:
            case WallpaperTransitionState.PausedBlack:
            case WallpaperTransitionState.Paused:
                ResumeWallpaper();
                break;
        }

        PublishPlaybackState();
    }

    public void StopWallpaper()
    {
        ThrowIfDisposed();
        if (!_hasVideo || _transition.State is WallpaperTransitionState.Stopped or WallpaperTransitionState.Paused)
        {
            if (_hasVideo && _transition.State == WallpaperTransitionState.Paused)
            {
                FinishStop();
            }
            return;
        }

        DiagnosticLog.Write("playback", "Stop requested");
        _transition.TransitionTo(WallpaperTransitionState.Stopping, 0,
            GetTransitionDuration(StopVideoFadeOutDurationMilliseconds, 0), FinishStop);
        PublishPlaybackState();
    }

    private void StartVideoUnderBlack(string videoPath, long generation)
    {
        if (generation != Volatile.Read(ref _startupGeneration))
        {
            DiagnosticLog.Write("startup", $"stale startup callback ignored generation={generation}");
            return;
        }

        _transition.SetImmediate(WallpaperTransitionState.Preparing, 0);
        _endOfStreamReached = false;
        _endingTransitionStarted = false;
        _endingFadeCompleted = false;
        _endingTransitionCompleted = false;
        _lastAvSyncLogUtc = DateTime.MinValue;
        _startupVideoPath = videoPath;
        _startupRetryCount = 0;
        _startupStopwatch = Stopwatch.StartNew();
        _lastVideoPresentUtc = DateTime.UtcNow;
        _lowVideoBufferSinceUtc = DateTime.MinValue;
        _lastGc0 = GC.CollectionCount(0);
        _lastGc1 = GC.CollectionCount(1);
        _lastGc2 = GC.CollectionCount(2);
        _lastStartupWatchdogUtc = DateTime.UtcNow;
        try
        {
            if (_hasVideo)
            {
                _decoder.StopAndRelease();
            }

            _decoder.Open(videoPath, IsLooping, _renderer.Device);
            _renderer.RenderBlack();
            _firstFrameReady = false;
            _hasVideo = true;
            ShowRenderWindow();
            _frameTimer.Start();
            _progressTimer.Start();
            DiagnosticLog.Write("media-foundation", "video prepared; waiting for first decoded frame before fade-in");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("media-foundation", $"open failed: {ex}");
            _transition.SetImmediate(WallpaperTransitionState.Error, 0);
        }
        LogDesktopHierarchy("play-after-black");
        PublishPlaybackState();
    }

    private void PauseWallpaper()
    {
        if (!_hasVideo || !_decoder.IsPlaying)
        {
            return;
        }

        if (_transition.State == WallpaperTransitionState.Pausing)
        {
            return;
        }

        DiagnosticLog.Write("playback", "Pause requested");
        _transition.TransitionTo(WallpaperTransitionState.Pausing, 0,
            GetTransitionDuration(PauseVideoFadeOutDurationMilliseconds, 0), CompletePauseTransition);
        PublishPlaybackState();
    }

    private void ResumeWallpaper()
    {
        if (!_hasVideo)
        {
            return;
        }

        var state = _transition.State;
        if (state == WallpaperTransitionState.Pausing)
        {
            _transition.TransitionTo(WallpaperTransitionState.Resuming, 1,
                GetTransitionDuration(VideoResumeFadeInDurationMilliseconds, 1), SetPlayingState);
            return;
        }

        if (state is not WallpaperTransitionState.Paused and not WallpaperTransitionState.PausedBlack)
        {
            return;
        }

        _endOfStreamReached = false;
        _endingTransitionStarted = false;
        _endingFadeCompleted = false;
        _endingTransitionCompleted = false;
        _firstFrameReady = false;
        _startupRetryCount = 0;
        _startupStopwatch = Stopwatch.StartNew();
        _lastStartupWatchdogUtc = DateTime.UtcNow;
        _transition.SetImmediate(WallpaperTransitionState.ResumingPrepare, 0);
        ShowRenderWindow();
        LogDesktopHierarchy("resume-show");
        _decoder.PrepareResume();
        _frameTimer.Start();
        _progressTimer.Start();
        PublishPlaybackState();
    }

    private void SetPlayingState()
    {
        _transition.SetImmediate(WallpaperTransitionState.Playing, 1);
        PublishPlaybackState();
    }

    private void Decoder_FrameAvailable(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _frameTimerRestartQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _frameTimerRestartQueued, 0);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _frameTimerRestartQueued, 0);
            if (!_disposed && _hasVideo)
            {
                _frameTimer.Start();
            }
        }));
    }

    private void CompletePauseTransition()
    {
        if (_transition.State != WallpaperTransitionState.Pausing)
        {
            return;
        }

        _frameTimer.Stop();
        _progressTimer.Stop();
        _decoder.Pause();
        DiagnosticLog.Write("playback", $"Decoder paused at {TimeSpan.FromSeconds(_decoder.PositionSeconds):hh\\:mm\\:ss\\.fff}");
        _transition.SetImmediate(WallpaperTransitionState.PausedBlack, 0);
        RestoreOriginalWallpaper();
        _transition.SetImmediate(WallpaperTransitionState.Paused, 0);
        LogDesktopHierarchy("pause-complete");
        PublishPlaybackState();
    }

    private void FinishStop()
    {
        _frameTimer.Stop();
        _progressTimer.Stop();
        _decoder.StopAndRelease();
        _hasVideo = false;
        _firstFrameReady = false;
        _endOfStreamReached = false;
        _endingTransitionStarted = false;
        _endingFadeCompleted = false;
        _endingTransitionCompleted = false;
        RestoreOriginalWallpaper();
        _transition.SetImmediate(WallpaperTransitionState.Stopped, 0);
        DiagnosticLog.Write("playback", "Stopped");
        LogDesktopHierarchy("stop-complete");
        PublishPlaybackState();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        _decoder.SetPosition(Math.Clamp(position.TotalSeconds, 0, _decoder.DurationSeconds));
        PublishPlaybackState();
    }

    public void ToggleMuted()
    {
        ThrowIfDisposed();
        _decoder.SetMuted(!_decoder.IsMuted);
        PublishPlaybackState();
    }

    public void ToggleLooping()
    {
        ThrowIfDisposed();
        IsLooping = !IsLooping;
        _decoder.SetLooping(IsLooping);
        PublishPlaybackState();
    }

    internal void EnsureDesktopHost()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        var detector = new DesktopHostDetector();
        var snapshot = detector.Capture(refreshShell: true);
        detector.Log(snapshot);

        var progman = snapshot.Progman;
        _desktopShellProcessId = snapshot.ExplorerProcessId;
        _desktopHost = snapshot.WallpaperHost;
        _desktopIconHost = snapshot.IconHost == IntPtr.Zero ? progman : snapshot.IconHost;
        _desktopIconView = snapshot.IconView;

        if (_desktopIconView == IntPtr.Zero)
        {
            _desktopIconView = FindWindowEx(_desktopIconHost, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (_desktopHost == IntPtr.Zero || _desktopIconView == IntPtr.Zero)
        {
            throw new InvalidOperationException("没有找到可用的 Explorer 桌面图标层或 WorkerW 宿主。");
        }

        // On Windows 11, Progman may use NOREDIRECTIONBITMAP while the icon
        // view is layered.  A normal child of the wallpaper WorkerW is then
        // presentable (Present succeeds) but invisible.  Use an ownerless
        // layered child of the icon host and place it below DefView, above
        // Explorer's wallpaper WorkerW.  Explorer's own HWNDs are untouched.
        var raisedDesktop = IsRaisedDesktopLayout(progman, _desktopIconHost, _desktopIconView, _desktopHost);
        _renderParent = raisedDesktop ? _desktopIconHost : _desktopHost;
        DiagnosticLog.Write("wallpaper", $"desktop layout raised={raisedDesktop} render_parent={DescribeWindow(_renderParent)} icon_worker={DescribeWindow(_desktopHost)}");

        _monitorMetrics = DpiHelper.GetPrimaryMonitor();
        _monitorHandle = _monitorMetrics.Handle;
        foreach (var monitor in DpiHelper.EnumerateMonitors())
        {
            DiagnosticLog.Write("dpi", $"Monitor=0x{monitor.Handle.ToInt64():X} Bounds={monitor.Left},{monitor.Top},{monitor.Right},{monitor.Bottom} Work={monitor.WorkLeft},{monitor.WorkTop},{monitor.WorkRight},{monitor.WorkBottom} Dpi={monitor.DpiX}x{monitor.DpiY} Scale={monitor.ScaleX:0.##}x{monitor.ScaleY:0.##}");
        }

        var width = _monitorMetrics.Handle == IntPtr.Zero ? GetSystemMetrics(SmCxScreen) : _monitorMetrics.Width;
        var height = _monitorMetrics.Handle == IntPtr.Zero ? GetSystemMetrics(SmCyScreen) : _monitorMetrics.Height;
        // Use a native child HWND rather than a WinForms Form.  WinForms
        // rejects WS_EX_LAYERED for a non-top-level Form, while CreateWindowEx
        // creates the exact ownerless layered child required by the raised
        // desktop compositor path.
        EnsureRenderWindowClass();
        _windowHandle = CreateWindowEx(
            WsExLayered | WsExNoActivate,
            RenderWindowClass,
            "OliviaWallpaperRenderWindow",
            WsPopup,
            0,
            0,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"无法创建 WallpaperRenderWindow，Win32Error={Marshal.GetLastWin32Error()}");
        }

        // Keep the native window hidden while changing it from an ownerless
        // popup into a true child.  This avoids a CreateWindowEx failure seen
        // when Progman is used as the initial parent by a WPF process.
        var style = GetWindowLong(_windowHandle, GwlStyle);
        SetWindowLong(_windowHandle, GwlStyle, (style & ~WsPopup) | WsChild);

        var candidates = raisedDesktop
            ? new List<IntPtr> { _renderParent }
            : new List<IntPtr> { _desktopHost };
        if (!raisedDesktop)
        {
            candidates.AddRange(snapshot.Windows
                .Where(window => window.ClassName == "WorkerW"
                    && window.ProcessId == _desktopShellProcessId
                    && window.Handle != _desktopHost)
                .Select(window => window.Handle));
            if (_desktopIconHost != _desktopHost)
            {
                candidates.Add(_desktopIconHost);
            }
        }

        var previousParent = IntPtr.Zero;
        var attachedHost = IntPtr.Zero;
        foreach (var candidate in candidates.Distinct())
        {
            previousParent = SetParent(_windowHandle, candidate);
            if (GetParent(_windowHandle) == candidate)
            {
                attachedHost = candidate;
                break;
            }
        }

        if (attachedHost == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法把渲染窗口挂到 Explorer 桌面宿主。");
        }

        _renderParent = attachedHost;
        if (!raisedDesktop)
        {
            _desktopHost = attachedHost;
        }
        ShowWindow(_windowHandle, SwShownoactivate);
        if (!SetLayeredWindowAttributes(_windowHandle, 0, 255, LwaAlpha))
        {
            DiagnosticLog.Write("wallpaper", $"SetLayeredWindowAttributes failed after attach hwnd=0x{_windowHandle.ToInt64():X} error={Marshal.GetLastWin32Error()}");
        }
        // DefView is topmost among the Progman children and the wallpaper
        // WorkerW follows it.  Inserting after DefView puts our render child
        // between the icon layer and the wallpaper layer.
        var insertAfter = raisedDesktop ? _desktopIconView : HwndBottom;
        SetWindowPos(_windowHandle, insertAfter, 0, 0, width, height, SwpNoActivate | SwpShowWindow);
        _renderer.Initialize(_windowHandle, width, height);
        var windowDpi = DpiHelper.GetWindowDpi(_windowHandle);
        if (windowDpi > 0)
        {
            _monitorMetrics = _monitorMetrics with { DpiX = windowDpi, DpiY = windowDpi };
        }
        _renderer.RenderBlack();
        LogDesktopHierarchy("after-attach");
        DiagnosticLog.Write("wallpaper", $"desktop attach requested child={_windowHandle} render_parent={_renderParent} icon_worker={_desktopHost} previous={previousParent} actual_parent={GetParent(_windowHandle)}");
        DiagnosticLog.Write("wallpaper", $"native player child created width={width} height={height} icon_host={_desktopIconHost} icon_view={_desktopIconView} attached={IsAttachedToDesktopHost} below_icons={IsBelowDesktopIcons} layered={(GetWindowLong(_windowHandle, GwlExStyle) & WsExLayered) != 0}");
        DiagnosticLog.Write("dpi", $"WallpaperRenderWindow physical_size={width}x{height} monitor=0x{_monitorHandle.ToInt64():X} dpi={_monitorMetrics.DpiX}x{_monitorMetrics.DpiY}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (ReferenceEquals(_activeInstance, this))
        {
            _activeInstance = null;
        }
        _progressTimer.Stop();
        _frameTimer.Stop();
        _decoder.FrameAvailable -= Decoder_FrameAvailable;
        _transition.Dispose();
        _decoder.Dispose();
        _renderer.Dispose();
        if (_windowHandle != IntPtr.Zero)
        {
            DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }
    }

    private TimeSpan GetTransitionDuration(int fullDurationMilliseconds, double targetFadeFactor)
    {
        var distance = Math.Abs(targetFadeFactor - _transition.FadeFactor);
        if (distance <= 0.0001)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(Math.Max(1, fullDurationMilliseconds * distance));
    }

    private void ShowRenderWindow()
    {
        if (_windowHandle == IntPtr.Zero || !IsWindow(_windowHandle))
        {
            return;
        }

        ShowWindow(_windowHandle, SwShownoactivate);
        DiagnosticLog.Write("wallpaper", $"WallpaperRenderWindow shown hwnd=0x{_windowHandle.ToInt64():X}");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        ThreadCpuDiagnostics.MarkWakeup("Olivia.DesktopMonitor");
        RequestMonitorRefresh();
    }

    private void RequestMonitorRefresh()
    {
        if (_disposed)
        {
            return;
        }

        ThreadCpuDiagnostics.MarkWakeup("Olivia.DesktopMonitor");
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshMonitorBounds();
            return;
        }

        _ = dispatcher.BeginInvoke(RefreshMonitorBounds);
    }

    private void RefreshMonitorBounds()
    {
        if (_disposed || _windowHandle == IntPtr.Zero || !IsWindow(_windowHandle))
        {
            return;
        }

        using var monitorActivity = ThreadCpuDiagnostics.StartActivity("Olivia.DesktopMonitor");
        var metrics = DpiHelper.GetPrimaryMonitor();
        if (metrics.Handle == IntPtr.Zero)
        {
            return;
        }

        var changed = metrics.Handle != _monitorHandle
            || metrics.Width != _monitorMetrics.Width
            || metrics.Height != _monitorMetrics.Height
            || metrics.DpiX != _monitorMetrics.DpiX
            || metrics.DpiY != _monitorMetrics.DpiY;
        if (!changed)
        {
            return;
        }

        _monitorMetrics = metrics;
        _monitorHandle = metrics.Handle;
        SetWindowPos(_windowHandle, _desktopIconHost == _renderParent ? _desktopIconView : HwndBottom, 0, 0, metrics.Width, metrics.Height, SwpNoActivate | SwpShowWindow);
        _renderer.Resize(metrics.Width, metrics.Height);
        DiagnosticLog.Write("dpi", $"WallpaperRenderWindow resized physical_size={metrics.Width}x{metrics.Height} monitor=0x{metrics.Handle.ToInt64():X} dpi={metrics.DpiX}x{metrics.DpiY} display_change=true");
    }

    private void RestoreOriginalWallpaper()
    {
        DiagnosticLog.Write("wallpaper", "RestoreOriginalWallpaper begin");
        if (_windowHandle != IntPtr.Zero && IsWindow(_windowHandle))
        {
            ShowWindow(_windowHandle, SwHide);
        }

        DiagnosticLog.Write("wallpaper", "RestoreOriginalWallpaper complete");
    }

    private void CompleteEndTransition()
    {
        if (_endingTransitionCompleted || !_endingFadeCompleted || !_endOfStreamReached)
        {
            if (!_endingFadeCompleted || !_endOfStreamReached)
            {
                DiagnosticLog.Write("wallpaper.transition", $"ending cleanup waiting fade_completed={_endingFadeCompleted} eos={_endOfStreamReached}");
            }

            return;
        }

        _endingTransitionCompleted = true;
        FinishStop();
    }

    private void OnEndingFadeCompleted()
    {
        _endingFadeCompleted = true;
        DiagnosticLog.Write("wallpaper.transition", "Ending fade completed");
        CompleteEndTransition();
    }

    private void StartEndingTransition()
    {
        if (IsLooping || _endingTransitionStarted || !_hasVideo
            || _transition.State is not WallpaperTransitionState.Playing
                and not WallpaperTransitionState.VideoFadeIn
                and not WallpaperTransitionState.Resuming
                and not WallpaperTransitionState.ResumeWallpaperFadeToBlack)
        {
            return;
        }

        _endingTransitionStarted = true;
        DiagnosticLog.Write("playback", $"Entering Ending position={_decoder.PositionSeconds:0.###} duration={_decoder.DurationSeconds:0.###}");
        DiagnosticLog.Write("wallpaper.transition", "Ending fade started");
        _transition.TransitionTo(WallpaperTransitionState.Ending, 0,
            GetTransitionDuration(EndVideoFadeOutDurationMilliseconds, 0), OnEndingFadeCompleted);
    }

    private void RenderNextFrame()
    {
        if (_disposed || !_hasVideo)
        {
            return;
        }

        ThreadCpuDiagnostics.MarkWakeup("Olivia.UI");
        ThreadCpuDiagnostics.MarkWakeup("Olivia.VideoScheduler");
        GpuPipelineDiagnostics.MarkProgress("Olivia.UI", "RenderNextFrame");
        GpuPipelineDiagnostics.MarkProgress("Olivia.VideoScheduler", "RenderNextFrame");
        GpuPipelineDiagnostics.MarkProgress("Olivia.Render", "RenderTick");
        Interlocked.Increment(ref _renderTickCount);

        try
        {
            if (_transition.State is WallpaperTransitionState.Preparing
                or WallpaperTransitionState.ResumingPrepare
                or WallpaperTransitionState.Buffering)
            {
                byte[]? preparedPixels = null;
                Vortice.Direct3D11.ID3D11Texture2D? preparedTexture = null;
                uint preparedSubresource = 0;
                long preparedTimestamp = 0;
                bool preparedRead;
                using (ThreadCpuDiagnostics.StartActivity("Olivia.VideoScheduler"))
                {
                    preparedRead = _renderer.IsInitialized && !_firstFrameReady;
                    if (preparedRead)
                    {
                        preparedRead = _decoder.UsesGpuSurface
                            ? _decoder.TryReadPreparedGpuFrame(out preparedTexture, out preparedSubresource, out preparedTimestamp)
                            : _decoder.TryReadPreparedFrame(out preparedPixels, out preparedTimestamp);
                    }
                }

                if (preparedRead
                    && _decoder.Width > 0
                    && _decoder.Height > 0
                    && (preparedPixels is not null || preparedTexture is not null))
                {
                    try
                    {
                        if (preparedTexture is not null)
                        {
                            if (!_renderer.PresentGpuFrame(preparedTexture, preparedSubresource, _decoder.Width, _decoder.Height))
                            {
                                throw new InvalidOperationException("D3D11 VideoProcessor 无法呈现首帧。");
                            }
                        }
                        else
                        {
                            _renderer.PresentFrame(preparedPixels!, _decoder.Width, _decoder.Height);
                        }
                    }
                    finally
                    {
                        if (preparedTexture is not null)
                        {
                            _decoder.ReleaseGpuFrame(preparedTexture);
                        }
                        else if (preparedPixels is not null)
                        {
                            _decoder.ReleaseFrame(preparedPixels);
                        }
                    }
                    _decoder.MarkFirstFramePresented(preparedTimestamp / 10_000_000d);
                    _firstFrameReady = true;
                    _lastVideoPresentUtc = DateTime.UtcNow;
                    DiagnosticLog.Write("startup", $"FirstFrameGPUReady=true pts={preparedTimestamp / 10_000_000d:0.###}");
                }

                if (_firstFrameReady && _decoder.StartPreparedPlayback())
                {
                    DiagnosticLog.Write("startup", $"PlaybackPrerequisites=true transition={_transition.State} total_startup_ms={_startupStopwatch?.Elapsed.TotalMilliseconds:0.0}");
                    if (_transition.State == WallpaperTransitionState.Preparing)
                    {
                        _transition.TransitionTo(WallpaperTransitionState.VideoFadeIn, 1,
                            TimeSpan.FromMilliseconds(VideoFadeInDurationMilliseconds), SetPlayingState);
                    }
                    else if (_transition.State == WallpaperTransitionState.ResumingPrepare)
                    {
                        _transition.TransitionTo(WallpaperTransitionState.Resuming, 1,
                            TimeSpan.FromMilliseconds(VideoResumeFadeInDurationMilliseconds), SetPlayingState);
                    }
                    else
                    {
                        _transition.SetImmediate(WallpaperTransitionState.Playing, _transition.FadeFactor);
                        DiagnosticLog.Write("playback", "Buffering recovery completed; audio and video resumed together");
                    }
                }

                CheckStartupWatchdog();
                LogAvSyncIfDue();
                return;
            }

            if (!_decoder.IsPlaying)
            {
                return;
            }

            byte[]? pixels = null;
            Vortice.Direct3D11.ID3D11Texture2D? texture = null;
            uint textureSubresource = 0;
            long presentedTimestamp = 0;
            bool frameRead;
            using (ThreadCpuDiagnostics.StartActivity("Olivia.VideoScheduler"))
            {
                frameRead = _decoder.UsesGpuSurface
                    ? _decoder.TryReadGpuFrame(out texture, out textureSubresource, out presentedTimestamp)
                    : _decoder.TryReadFrame(out pixels, out presentedTimestamp);
            }

            if (frameRead
                && _decoder.Width > 0
                && _decoder.Height > 0
                && (pixels is not null || texture is not null))
            {
                try
                {
                    if (texture is not null)
                    {
                        if (!_renderer.PresentGpuFrame(texture, textureSubresource, _decoder.Width, _decoder.Height))
                        {
                            throw new InvalidOperationException("D3D11 VideoProcessor 无法呈现视频帧。");
                        }
                    }
                    else
                    {
                        _renderer.PresentFrame(pixels!, _decoder.Width, _decoder.Height);
                    }
                }
                finally
                {
                    if (texture is not null)
                    {
                        _decoder.ReleaseGpuFrame(texture);
                    }
                    else if (pixels is not null)
                    {
                        _decoder.ReleaseFrame(pixels);
                    }
                }
                _lastVideoPresentUtc = DateTime.UtcNow;
                var schedulerNow = DateTime.UtcNow;
                if (schedulerNow - _lastSchedulerLogUtc >= TimeSpan.FromMilliseconds(500))
                {
                    _lastSchedulerLogUtc = schedulerNow;
                    var masterClock = _decoder.MasterClockSeconds;
                    var candidatePts = presentedTimestamp / 10_000_000d;
                    DiagnosticLog.Write("scheduler", $"MasterClock={masterClock:0.000} CandidatePTS={candidatePts:0.000} DeltaMs={(candidatePts - masterClock) * 1000:0.0}");
                }
                if (!_firstFrameReady)
                {
                    _firstFrameReady = true;
                }

                if (!IsLooping
                    && !_endingTransitionStarted
                    && _decoder.DurationSeconds > 0
                    && _decoder.PositionSeconds >= _decoder.DurationSeconds - (EndVideoFadeOutDurationMilliseconds / 1000d))
                {
                    StartEndingTransition();
                }
            }
            else if (_decoder.EndOfStream && !IsLooping)
            {
                if (!_endOfStreamReached)
                {
                    _endOfStreamReached = true;
                    DiagnosticLog.Write("playback", "EOS reached");
                }

                StartEndingTransition();
                CompleteEndTransition();
            }
            else if (_decoder.VideoQueueDepth == 0
                && _transition.State == WallpaperTransitionState.Playing)
            {
                // A decode wake-up restarts the timer when the queue receives
                // its first frame, avoiding empty 16 ms scheduler ticks.
                _frameTimer.Stop();
            }

            CheckVideoBufferHealth();
            CheckVideoStall();
        }
        catch (Exception ex)
        {
            _frameTimer.Stop();
            _transition.SetImmediate(WallpaperTransitionState.Error, 0);
            DiagnosticLog.Write("media-foundation", $"frame render failed: {ex}");
        }
        finally
        {
            LogAvSyncIfDue();
            LogPerformanceIfDue();
        }
    }

    private void LogPerformanceIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPerformanceLogUtc < TimeSpan.FromSeconds(1) || !_hasVideo)
        {
            return;
        }

        _lastPerformanceLogUtc = now;
        var counters = _decoder.GetPerformanceCounters(_renderTickCount, _renderer.PresentCallCount);
        using var cpu = Process.GetCurrentProcess();
        var cpuTime = cpu.TotalProcessorTime;
        var cpuPercent = 0d;
        if (_lastCpuSampleUtc != DateTime.MinValue)
        {
            var wallSeconds = Math.Max(0.001, (now - _lastCpuSampleUtc).TotalSeconds);
            cpuPercent = Math.Clamp((cpuTime - _lastCpuTime).TotalSeconds / (wallSeconds * Environment.ProcessorCount) * 100d, 0, 100);
        }

        _lastCpuSampleUtc = now;
        _lastCpuTime = cpuTime;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var gc0Delta = gc0 - _lastGc0;
        var gc1Delta = gc1 - _lastGc1;
        var gc2Delta = gc2 - _lastGc2;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
        DiagnosticLog.Write("performance", $"PlaybackPerf CPU={cpuPercent:0.0}% VideoPipeline=MF-NV12-D3D11-or-CPU-fallback SourceFPS={counters.SourceFrameRate:0.##} VideoDecodedFPS={counters.DecodedFramesPerSecond:0.##} VideoPresentedFPS={counters.PresentedFramesPerSecond:0.##} RenderTicks={counters.RenderTicksPerSecond:0.##} PresentCalls={counters.PresentCallsPerSecond:0.##} VideoQueueDepth={_decoder.VideoQueueDepth} AudioBufferedMs={_decoder.AudioBufferedDuration.TotalMilliseconds:0.0} ReadSampleAvgMs={counters.ReadSampleAverageMilliseconds:0.0} ReadSampleMaxMs={counters.ReadSampleMaxMilliseconds:0.0} PresentAvgMs={_renderer.LastPresentMilliseconds:0.0} PresentMaxMs={_renderer.MaxPresentMilliseconds:0.0} DroppedFrames={_decoder.GetAvSyncDiagnostics().DroppedFrames} GC0={gc0Delta} GC1={gc1Delta} GC2={gc2Delta} DPI={_monitorMetrics.DpiX} Scaling={_monitorMetrics.ScaleX:0.##} RenderWidth={_renderer.RenderWidth} RenderHeight={_renderer.RenderHeight}");
        ThreadCpuDiagnostics.Sample();
    }

    private void LogAvSyncIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAvSyncLogUtc < TimeSpan.FromMilliseconds(500) || !_hasVideo)
        {
            return;
        }

        _lastAvSyncLogUtc = now;
        var sync = _decoder.GetAvSyncDiagnostics();
        DiagnosticLog.Write("avsync", $"MasterClock={sync.MasterClockSeconds:0.000} AudioPosition={sync.AudioPositionSeconds:0.000} VideoPTS={sync.VideoDecodedPositionSeconds:0.000} VideoPresentedPTS={sync.VideoPresentedPositionSeconds:0.000} AVDiffMs={sync.AvDiffMilliseconds:0.0} VideoQueue={sync.VideoQueueDepth} DroppedFrames={sync.DroppedFrames} DecodeTimeMs={sync.LastDecodeMilliseconds:0.0} MaxDecodeMs={sync.MaxDecodeMilliseconds:0.0} PresentTimeMs={_renderer.LastPresentMilliseconds:0.0} MaxPresentMs={_renderer.MaxPresentMilliseconds:0.0}");
    }

    private void CheckStartupWatchdog()
    {
        if (_startupStopwatch is null
            || _transition.State is not WallpaperTransitionState.Preparing
                and not WallpaperTransitionState.ResumingPrepare
                and not WallpaperTransitionState.Buffering)
        {
            return;
        }

        var elapsed = _startupStopwatch.Elapsed;
        var timeout = _transition.State == WallpaperTransitionState.ResumingPrepare
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromSeconds(3);
        if (elapsed < timeout
            || DateTime.UtcNow - _lastStartupWatchdogUtc < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _lastStartupWatchdogUtc = DateTime.UtcNow;
        DiagnosticLog.Write("startup", $"STARTUP_STALL stage=first-frame elapsed_ms={elapsed.TotalMilliseconds:0} generation={_startupGeneration} audio_started={_decoder.AudioClockStarted} first_frame_ready={_firstFrameReady} renderer_ready={_renderer.IsInitialized} queue_depth={_decoder.VideoQueueDepth}");
        if (_startupRetryCount >= 1)
        {
            FailStartup("startup timeout after one retry");
            return;
        }

        _startupRetryCount++;
        DiagnosticLog.Write("startup", $"retry={_startupRetryCount} resetting decoder");
        try
        {
            if (_transition.State == WallpaperTransitionState.ResumingPrepare)
            {
                _decoder.PrepareResume();
            }
            else if (_transition.State == WallpaperTransitionState.Buffering)
            {
                _decoder.EnterBuffering();
                _firstFrameReady = false;
            }
            else if (!string.IsNullOrWhiteSpace(_startupVideoPath))
            {
                _decoder.StopAndRelease();
                _decoder.Open(_startupVideoPath, IsLooping, _renderer.Device);
            }

            _firstFrameReady = false;
            _startupStopwatch = Stopwatch.StartNew();
        }
        catch (Exception ex)
        {
            FailStartup($"retry failed: {ex.Message}");
        }
    }

    private void FailStartup(string reason)
    {
        DiagnosticLog.Write("startup", $"STARTUP_STALL stage=failed reason={reason} generation={_startupGeneration} audio_started={_decoder.AudioClockStarted} first_frame_ready={_firstFrameReady} renderer_ready={_renderer.IsInitialized} queue_depth={_decoder.VideoQueueDepth}");
        _frameTimer.Stop();
        _progressTimer.Stop();
        _decoder.StopAndRelease();
        _hasVideo = false;
        _transition.SetImmediate(WallpaperTransitionState.Error, 0);
        PublishPlaybackState();
    }

    private void CheckVideoStall()
    {
        if (!_decoder.IsPlaying || !_decoder.HasAudio
            || _transition.State is not WallpaperTransitionState.Playing
                and not WallpaperTransitionState.VideoFadeIn
                and not WallpaperTransitionState.Resuming)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _lastVideoPresentUtc;
        if (elapsed < TimeSpan.FromMilliseconds(500) || _decoder.VideoQueueDepth > 0)
        {
            return;
        }

        DiagnosticLog.Write("stall", $"VIDEO_STALL stage=queue_or_decode elapsed_ms={elapsed.TotalMilliseconds:0} state={_transition.State} media_time={_decoder.MasterClockSeconds:0.###} queue_depth={_decoder.VideoQueueDepth} audio_buffered_ms={_decoder.AudioBufferedDuration.TotalMilliseconds:0.0} last_decode_ago_ms={_decoder.LastDecodeAgeMilliseconds:0.0} last_video_present_ago_ms={elapsed.TotalMilliseconds:0.0} last_audio_submit_ago_ms={_decoder.LastAudioSubmitAgeMilliseconds:0.0} decode_stage=ReadSample render_stage=Present audio_stage=WASAPI");
        if (!_decoder.BeginBuffering())
        {
            return;
        }
        _startupRetryCount = 0;
        _startupStopwatch = Stopwatch.StartNew();
        _transition.SetImmediate(WallpaperTransitionState.Buffering, _transition.FadeFactor);
    }

    private void CheckVideoBufferHealth()
    {
        if (!_decoder.IsPlaying || !_decoder.HasAudio
            || _transition.State is not WallpaperTransitionState.Playing
                and not WallpaperTransitionState.VideoFadeIn
                and not WallpaperTransitionState.Resuming
            || _decoder.EndOfStream)
        {
            _lowVideoBufferSinceUtc = DateTime.MinValue;
            return;
        }

        var queueLow = _decoder.VideoQueueDepth <= 1;
        var audioLow = _decoder.AudioBufferedDuration <= TimeSpan.FromMilliseconds(80);
        if (!queueLow || !audioLow)
        {
            _lowVideoBufferSinceUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (_lowVideoBufferSinceUtc == DateTime.MinValue)
        {
            _lowVideoBufferSinceUtc = now;
            return;
        }

        if (now - _lowVideoBufferSinceUtc < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        _lowVideoBufferSinceUtc = DateTime.MinValue;
        if (!_decoder.BeginBuffering())
        {
            return;
        }

        _startupRetryCount = 0;
        _startupStopwatch = Stopwatch.StartNew();
        _transition.SetImmediate(WallpaperTransitionState.Buffering, _transition.FadeFactor);
        DiagnosticLog.Write("playback", $"Buffering started low_water queue_depth={_decoder.VideoQueueDepth} audio_buffered_ms={_decoder.AudioBufferedDuration.TotalMilliseconds:0.0}");
    }

    private void RefreshPlaybackState()
    {
        if (_disposed)
        {
            return;
        }

        PublishPlaybackState();
    }

    private void PublishPlaybackState()
    {
        if (_disposed)
        {
            return;
        }

        PlaybackStateChanged?.Invoke(this, new WallpaperPlaybackState(
            TimeSpan.FromSeconds(_decoder.PositionSeconds), TimeSpan.FromSeconds(_decoder.DurationSeconds),
            IsPlaybackActive, IsLooping, _decoder.IsMuted));
    }

    private bool IsPlaybackActive => _transition.State is WallpaperTransitionState.Preparing
        or WallpaperTransitionState.WallpaperFadeToBlack
        or WallpaperTransitionState.VideoFadeIn
        or WallpaperTransitionState.Playing
        or WallpaperTransitionState.Pausing
        or WallpaperTransitionState.ResumeWallpaperFadeToBlack
        or WallpaperTransitionState.ResumingPrepare
        or WallpaperTransitionState.Resuming
        or WallpaperTransitionState.Buffering
        or WallpaperTransitionState.Ending
        or WallpaperTransitionState.Stopping;

    private void SetVideoFadeFactor(double factor)
    {
        _renderer.FadeFactor = (float)Math.Clamp(factor, 0, 1);
        _renderer.RenderBlack();
    }

    private void LogDesktopHierarchy(string stage)
    {
        var progman = FindWindow("Progman", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var sysList = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        var iconHost = _desktopIconHost;
        DiagnosticLog.Write("desktop", $"stage={stage} Progman={DescribeWindow(progman)} DefView={DescribeWindow(defView)} IconHost={DescribeWindow(iconHost)} IconWorkerW={DescribeWindow(_desktopHost)} SysListView={DescribeWindow(sysList)}");
        DiagnosticLog.Write("wallpaper", $"stage={stage} WallpaperRenderWindow={DescribeWindow(_windowHandle)} RenderParent={DescribeWindow(_renderParent)} VideoOutput=MediaFoundation+D3D11 TransitionState={_transition.State} FadeFactor={_transition.FadeFactor:0.###}");
    }

    private static string DescribeWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return "0";
        }

        var className = new StringBuilder(128);
        GetClassName(handle, className, className.Capacity);
        GetWindowThreadProcessId(handle, out var processId);
        GetWindowRect(handle, out var rect);
        var previous = GetWindow(handle, GwHwndPrev);
        var next = GetWindow(handle, GwHwndNext);
        var owner = GetWindow(handle, GwOwner);
        return $"0x{handle.ToInt64():X}[Parent=0x{GetParent(handle).ToInt64():X},Owner=0x{owner.ToInt64():X},Class={className},Visible={IsWindowVisible(handle)},Rect={rect.Left},{rect.Top}-{rect.Right},{rect.Bottom},Prev=0x{previous.ToInt64():X},Next=0x{next.ToInt64():X},Pid={processId}]";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DesktopWallpaperWindow));
        }
    }

    private bool FindDesktopHost(IntPtr topHandle, IntPtr _)
    {
        var shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shellView == IntPtr.Zero || !IsOwnedByDesktopShell(topHandle))
        {
            return true;
        }

        if (_desktopIconView == IntPtr.Zero)
        {
            _desktopIconHost = topHandle;
            _desktopIconView = shellView;
        }
        if (_desktopHost == IntPtr.Zero)
        {
            _desktopHost = FindWorkerWAfter(topHandle);
        }
        return _desktopHost == IntPtr.Zero;
    }

    private IntPtr FindWorkerWAfter(IntPtr after)
    {
        for (var worker = FindWindowEx(IntPtr.Zero, after, "WorkerW", null);
             worker != IntPtr.Zero;
             worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
        {
            if (IsOwnedByDesktopShell(worker)
                && FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return worker;
            }
        }

        return IntPtr.Zero;
    }

    private IEnumerable<IntPtr> EnumerateDesktopHosts()
    {
        if (_desktopHost != IntPtr.Zero)
        {
            yield return _desktopHost;
        }

        for (var worker = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
             worker != IntPtr.Zero;
             worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
        {
            if (worker != _desktopHost
                && IsOwnedByDesktopShell(worker)
                && FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                yield return worker;
            }
        }

        if (_desktopIconHost != IntPtr.Zero && _desktopIconHost != _desktopHost)
        {
            yield return _desktopIconHost;
        }
    }

    private bool IsOwnedByDesktopShell(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId == _desktopShellProcessId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    private delegate bool EnumWindowsProc(IntPtr topHandle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WindowProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr DeviceContext;
        public bool Erase;
        public RECT PaintRect;
        public bool Restore;
        public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr handle, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr handle, ref PaintStruct paint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr handle);

    private static void EnsureRenderWindowClass()
    {
        if (_renderWindowClassAtom != 0)
        {
            return;
        }

        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = CsHRedraw | CsVRedraw,
            WindowProcedure = RenderWindowProc,
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(IddcArrow)),
            ClassName = RenderWindowClass,
        };

        _renderWindowClassAtom = RegisterClassEx(ref windowClass);
        if (_renderWindowClassAtom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                throw new InvalidOperationException($"无法注册 WallpaperRenderWindow 类，Win32Error={error}");
            }
        }
    }

    private static IntPtr RenderWndProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDisplayChange || message == WmDpiChanged)
        {
            _activeInstance?.RequestMonitorRefresh();
            return IntPtr.Zero;
        }

        if (message == WmEraseBkgnd)
        {
            return new IntPtr(1);
        }

        if (message == WmPaint)
        {
            var paint = new PaintStruct();
            BeginPaint(handle, out paint);
            EndPaint(handle, ref paint);
            return IntPtr.Zero;
        }

        return DefWindowProc(handle, message, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static bool IsRaisedDesktopLayout(IntPtr progman, IntPtr iconHost, IntPtr iconView, IntPtr wallpaperHost)
    {
        if (progman == IntPtr.Zero || iconHost == IntPtr.Zero || iconView == IntPtr.Zero || wallpaperHost == IntPtr.Zero
            || GetParent(wallpaperHost) != iconHost)
        {
            return false;
        }

        var progmanExStyle = GetWindowLong(progman, GwlExStyle);
        var iconViewExStyle = GetWindowLong(iconView, GwlExStyle);
        return (progmanExStyle & WsExNoRedirectionBitmap) != 0
            && (iconViewExStyle & WsExLayered) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
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
        player.windowlessVideo = true;
        player.stretchToFit = true;
        player.settings.setMode("loop", loop);
        player.URL = path;
        player.windowlessVideo = true;
        player.controls.play();
        player.windowlessVideo = true;
        try { DiagnosticLog.Write("wallpaper", $"WMP windowlessVideo={Convert.ToBoolean(player.windowlessVideo)}"); } catch { }
    }

    public double PositionSeconds
    {
        get
        {
            try { return Math.Max(0, Convert.ToDouble(Player.controls.currentPosition)); }
            catch (Exception) { return 0; }
        }
    }

    public void SetPosition(double value)
    {
        try { Player.controls.currentPosition = value; }
        catch (Exception) { }
    }

    public double DurationSeconds
    {
        get
        {
            try { return Math.Max(0, Convert.ToDouble(Player.currentMedia.duration)); }
            catch (Exception) { return 0; }
        }
    }

    public int PlayState
    {
        get
        {
            try { return Convert.ToInt32(Player.playState); }
            catch (Exception) { return 0; }
        }
    }

    public bool IsMuted
    {
        get
        {
            try { return Convert.ToBoolean(Player.settings.mute); }
            catch (Exception) { return false; }
        }
    }

    public void SetMuted(bool value)
    {
        try { Player.settings.mute = value; }
        catch (Exception) { }
    }

    public void SetLooping(bool value)
    {
        try { Player.settings.setMode("loop", value); }
        catch (Exception) { }
    }

    public void Pause() => Player.controls.pause();

    public void Play() => Player.controls.play();

    public void StopAndRelease()
    {
        try
        {
            Player.controls.stop();
            Player.URL = string.Empty;
        }
        catch (Exception)
        {
        }
    }

    private dynamic Player => GetOcx() ?? throw new InvalidOperationException("Windows Media Player 控件尚未初始化。");
}
