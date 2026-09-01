using System.Windows.Threading;

namespace OliviaLetterOverlay;

public enum WallpaperTransitionState
{
    Stopped,
    Preparing,
    WallpaperFadeToBlack,
    VideoFadeIn,
    Playing,
    Pausing,
    PausedBlack,
    Paused,
    ResumingPrepare,
    ResumeWallpaperFadeToBlack,
    Resuming,
    Buffering,
    Ending,
    Stopping,
    Error,
}

/// <summary>
/// 唯一的黑场过渡控制器。渲染层只消费 FadeFactor，不关心 Play/Pause/Stop 的具体时序。
/// </summary>
public sealed class WallpaperTransitionController : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _from;
    private double _to;
    private DateTime _startedAt;
    private TimeSpan _duration;
    private Action? _completed;
    private int _lastProgressBucket = -1;
    private bool _disposed;

    public WallpaperTransitionController()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public WallpaperTransitionState State { get; private set; } = WallpaperTransitionState.Stopped;
    public double FadeFactor { get; private set; }
    public byte BlackOverlayOpacity => (byte)Math.Round((1 - FadeFactor) * 255);
    public double StartFadeFactor { get; private set; }
    public double TargetFadeFactor { get; private set; }
    public DateTime StartTime { get; private set; }
    public TimeSpan Duration => _duration;

    public event EventHandler? Updated;

    public void SetImmediate(WallpaperTransitionState state, double fadeFactor)
    {
        ThrowIfDisposed();
        _timer.Stop();
        _completed = null;
        State = state;
        FadeFactor = Math.Clamp(fadeFactor, 0, 1);
        StartFadeFactor = FadeFactor;
        TargetFadeFactor = FadeFactor;
        StartTime = DateTime.UtcNow;
        _duration = TimeSpan.Zero;
        _lastProgressBucket = -1;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void TransitionTo(WallpaperTransitionState state, double targetFadeFactor, TimeSpan duration, Action? completed = null)
    {
        ThrowIfDisposed();
        _from = FadeFactor;
        _to = Math.Clamp(targetFadeFactor, 0, 1);
        _duration = duration <= TimeSpan.Zero ? TimeSpan.Zero : duration;
        _startedAt = DateTime.UtcNow;
        StartFadeFactor = _from;
        TargetFadeFactor = _to;
        StartTime = _startedAt;
        _lastProgressBucket = -1;
        _completed = completed;
        State = state;
        DiagnosticLog.Write("wallpaper.transition", $"start state={state} from={_from:0.###} to={_to:0.###} duration_ms={_duration.TotalMilliseconds:0}");

        if (_duration == TimeSpan.Zero || Math.Abs(_from - _to) < 0.0001)
        {
            FadeFactor = _to;
            Updated?.Invoke(this, EventArgs.Empty);
            Complete();
            return;
        }

        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _completed = null;
    }

    private void Tick()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        var smooth = progress * progress * (3 - 2 * progress);
        FadeFactor = _from + ((_to - _from) * smooth);
        Updated?.Invoke(this, EventArgs.Empty);
        var progressBucket = (int)Math.Floor(progress * 20);
        if (progressBucket != _lastProgressBucket)
        {
            _lastProgressBucket = progressBucket;
            DiagnosticLog.Write("wallpaper.transition", $"progress={progress:0.##} fade={FadeFactor:0.###} state={State}");
        }
        if (progress >= 1)
        {
            Complete();
        }
    }

    private void Complete()
    {
        _timer.Stop();
        FadeFactor = _to;
        Updated?.Invoke(this, EventArgs.Empty);
        var callback = _completed;
        _completed = null;
        DiagnosticLog.Write("wallpaper.transition", $"complete state={State} factor={FadeFactor:0.###}");
        callback?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _completed = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WallpaperTransitionController));
        }
    }
}
