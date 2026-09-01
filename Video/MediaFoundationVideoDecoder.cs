using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;

namespace OliviaLetterOverlay.Video;

internal readonly record struct AvSyncDiagnostics(
    double MasterClockSeconds,
    double AudioPositionSeconds,
    double VideoDecodedPositionSeconds,
    double VideoPresentedPositionSeconds,
    double AvDiffMilliseconds,
    int VideoQueueDepth,
    long DroppedFrames,
    double LastDecodeMilliseconds,
    double MaxDecodeMilliseconds);

internal readonly record struct VideoPerformanceCounters(
    double SourceFrameRate,
    double DecodedFramesPerSecond,
    double PresentedFramesPerSecond,
    double RenderTicksPerSecond,
    double PresentCallsPerSecond);

/// <summary>
/// 第一阶段的 Media Foundation Source Reader 解码器。
/// 输出 RGB32 帧给 D3D11 Renderer，避免 WMP/EVR 创建独立全屏 HWND。
/// </summary>
internal sealed class MediaFoundationVideoDecoder : IDisposable
{
    private const int MaxVideoQueueDepth = 4;
    private const double EarlyThresholdSeconds = 0.004;
    private const double LateThresholdSeconds = 0.040;
    private const double AudioBufferTargetMilliseconds = 100;
    private static readonly object StartupGate = new();
    private static bool _mediaFoundationStarted;
    private readonly object _commandGate = new();
    private readonly object _videoQueueGate = new();
    private readonly Queue<VideoFrame> _videoQueue = new();
    private readonly ManualResetEventSlim _decodeWake = new(false);
    private IMFSourceReader? _reader;
    private CancellationTokenSource? _decodeCancellation;
    private Thread? _decodeThread;
    private bool _disposed;
    private bool _loop;
    private string? _path;
    private bool _firstFrameLogged;
    private WasapiAudioOutput? _audioOutput;
    private bool _audioStreamAvailable;
    private bool _audioEndOfStream;
    private int _audioNeedsPriming = 1;
    private int _audioStartGateOpen;
    private bool _audioFirstSampleLogged;
    private bool _decodePriming;
    private bool _startupFramePresented;
    private bool _startupFirstReadLogged;
    private bool _startupFirstSampleLogged;
    private Stopwatch? _startupStopwatch;
    private double _audioSeekTargetSeconds;
    private bool _videoEndOfStream;
    private double _videoSeekTargetSeconds;
    private long _droppedFrames;
    private double _positionSeconds;
    private double _decodedVideoPositionSeconds;
    private double _presentedVideoPositionSeconds;
    private readonly Stopwatch _fallbackClock = new();
    private double _lastDecodeMilliseconds;
    private double _maxDecodeMilliseconds;
    private double _sourceFrameRate;
    private long _decodedFrameCount;
    private long _presentedFrameCount;
    private long _lastCounterDecoded;
    private long _lastCounterPresented;
    private long _lastCounterRenderTicks;
    private long _lastCounterPresentCalls;
    private DateTime _lastCounterTimeUtc = DateTime.UtcNow;
    private bool _seekRequested;
    private double _pendingSeekSeconds;

    private static readonly Guid Nv12Subtype = new("3231564E-3961-11CE-8E00-00AA0055595A");

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double PositionSeconds => Volatile.Read(ref _positionSeconds);
    public double DurationSeconds { get; private set; }
    public bool IsPlaying
    {
        get => Volatile.Read(ref _isPlaying) != 0;
        private set => Volatile.Write(ref _isPlaying, value ? 1 : 0);
    }
    public bool IsMuted { get; private set; }
    public bool EndOfStream
    {
        get
        {
            lock (_videoQueueGate)
            {
                return _videoEndOfStream && _videoQueue.Count == 0;
            }
        }
    }
    public bool HasAudio => _audioStreamAvailable && _audioOutput is not null;
    public bool AudioClockStarted => _audioOutput?.IsClockStarted == true;
    public bool StartupAudioReady => !HasAudio || _audioOutput?.BufferedDuration > TimeSpan.Zero;
    public bool StartupFrameReady
    {
        get
        {
            lock (_videoQueueGate)
            {
                return _videoQueue.Count > 0 && _videoSeekTargetSeconds <= 0;
            }
        }
    }
    public bool StartupFramePresented => _startupFramePresented;
    public int VideoQueueDepth => GetVideoQueueDepth();
    public double MasterClockSeconds => GetMasterClockSeconds();
    public double AudioPositionSeconds => _audioOutput?.PlaybackPositionSeconds ?? 0;
    public double SourceFrameRate => _sourceFrameRate;

    private int _isPlaying;

    private readonly record struct VideoFrame(byte[] Pixels, long PresentationTime100Ns, long Duration100Ns);

    public void Open(string path, bool loop)
    {
        ThrowIfDisposed();
        path = System.Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("视频文件不存在。", path);
        }

        EnsureMediaFoundation();
        if (!StopDecodeThread())
        {
            throw new TimeoutException("旧的视频解码线程未能在安全超时内退出，已阻止替换 SourceReader。");
        }
        ReleaseAudio();
        ReleaseReader();
        _path = path;
        _loop = loop;
        Volatile.Write(ref _positionSeconds, 0);
        Volatile.Write(ref _decodedVideoPositionSeconds, 0);
        Volatile.Write(ref _presentedVideoPositionSeconds, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        _sourceFrameRate = 0;
        DurationSeconds = 0;
        // Startup is decode-only until the first valid video frame has been
        // rendered and the audio buffer is primed.  The playback clock must
        // not begin while that preparation is still in progress.
        IsPlaying = false;
        _decodePriming = true;
        _startupFramePresented = false;
        _startupFirstReadLogged = false;
        _startupFirstSampleLogged = false;
        _startupStopwatch = Stopwatch.StartNew();
        _firstFrameLogged = false;
        _audioEndOfStream = false;
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        _audioFirstSampleLogged = false;
        _audioSeekTargetSeconds = 0;
        _videoEndOfStream = false;
        _videoSeekTargetSeconds = 0;
        lock (_commandGate)
        {
            _seekRequested = false;
            _pendingSeekSeconds = 0;
        }
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        _sourceFrameRate = 0;
        _lastCounterDecoded = 0;
        _lastCounterPresented = 0;
        _lastCounterRenderTicks = 0;
        _lastCounterPresentCalls = 0;
        _lastCounterTimeUtc = DateTime.UtcNow;
        _lastDecodeMilliseconds = 0;
        _maxDecodeMilliseconds = 0;
        _fallbackClock.Restart();
        ClearVideoQueue();
        DiagnosticLog.Write("startup", $"OpenSource begin file={path}");

        using var attributes = MediaFactory.MFCreateAttributes(4);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true);
        DiagnosticLog.Write("media-foundation", "HardwareTransformsRequested=true SourceReader-DXVA=default");
        _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        DiagnosticLog.Write("startup", "VideoStreamSelected=true");
        ConfigureAudioStream();
        DiagnosticLog.Write("startup", $"AudioStreamSelected={HasAudio}");

        IMFMediaType? nativeType = null;
        try
        {
            nativeType = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            DiagnosticLog.Write("media-foundation", $"native {DescribeColorMetadata(nativeType)}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("media-foundation", $"native media type unavailable: {ex.Message}");
        }
        finally
        {
            nativeType?.Dispose();
        }

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

        using var currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var packedSize = currentType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        MediaFactory.UnpackSize(packedSize, out var width, out var height);
        Width = checked((int)width);
        Height = checked((int)height);
        _sourceFrameRate = TryGetFrameRate(currentType);
        try
        {
            var durationKey = new MediaAttributeKey<ulong>(PresentationDescriptionAttributeKeys.Duration);
            var duration100Ns = _reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, durationKey);
            DurationSeconds = Math.Max(0, duration100Ns / 10_000_000d);
            DiagnosticLog.Write("media-foundation", $"duration={DurationSeconds:0.###}s source=MF_PD_DURATION");
        }
        catch (Exception ex)
        {
            DurationSeconds = 0;
            DiagnosticLog.Write("media-foundation", $"duration unavailable: {ex.Message}");
        }
        DiagnosticLog.Write("media-foundation", $"output {DescribeColorMetadata(currentType)} dxgiUpload=B8G8R8A8_UNORM yuvConversion=MediaFoundation color-converted={currentType.GetGUID(MediaTypeAttributeKeys.Subtype) != Nv12Subtype}");
        DiagnosticLog.Write("media-foundation", $"opened path={path} size={Width}x{Height} output=RGB32 loop={_loop} source_fps={_sourceFrameRate:0.###} hardware_accelerated=dxva-requested");
        DiagnosticLog.Write("startup", $"SourceReady=true decoder_ready=true open_ms={_startupStopwatch?.Elapsed.TotalMilliseconds:0.0}");
        StartDecodeThread();
    }

    public bool TryReadFrame(out byte[]? pixels, out long timestamp100Ns)
    {
        ThrowIfDisposed();
        pixels = null;
        timestamp100Ns = 0;
        if (!IsPlaying)
        {
            return false;
        }

        if (HasAudio && _audioOutput?.IsClockStarted != true)
        {
            // Do not let the fallback wall clock run ahead while the audio
            // device is still being primed.  The first frame waits for the
            // real audio clock, so startup cannot create an artificial drop.
            return false;
        }

        var masterClock = GetMasterClockSeconds();
        lock (_videoQueueGate)
        {
            while (_videoQueue.Count > 0)
            {
                var next = _videoQueue.Peek();
                var frameSeconds = next.PresentationTime100Ns / 10_000_000d;
                var deltaSeconds = frameSeconds - masterClock;

                if (deltaSeconds < -LateThresholdSeconds)
                {
                    _videoQueue.Dequeue();
                    Interlocked.Increment(ref _droppedFrames);
                    continue;
                }

                // Do not present a frame before its MF timestamp.  Once the
                // audio clock has started, it is the only playback clock.
                if (HasAudio && _audioOutput?.IsClockStarted == true && deltaSeconds > EarlyThresholdSeconds)
                {
                    return false;
                }

                _videoQueue.Dequeue();
                pixels = next.Pixels;
                timestamp100Ns = next.PresentationTime100Ns;
                Interlocked.Increment(ref _presentedFrameCount);
                Volatile.Write(ref _positionSeconds, frameSeconds);
                Volatile.Write(ref _presentedVideoPositionSeconds, frameSeconds);
                _decodeWake.Set();
                return true;
            }

            return false;
        }
    }

    public bool TryReadPreparedFrame(out byte[]? pixels, out long timestamp100Ns)
    {
        ThrowIfDisposed();
        pixels = null;
        timestamp100Ns = 0;
        if (!_decodePriming)
        {
            return false;
        }

        lock (_videoQueueGate)
        {
            if (_videoQueue.Count == 0 || _videoSeekTargetSeconds > 0)
            {
                return false;
            }

            var frame = _videoQueue.Dequeue();
            pixels = frame.Pixels;
            timestamp100Ns = frame.PresentationTime100Ns;
            Interlocked.Increment(ref _presentedFrameCount);
            Volatile.Write(ref _decodedVideoPositionSeconds, frame.PresentationTime100Ns / 10_000_000d);
            _decodeWake.Set();
            return true;
        }
    }

    public void MarkFirstFramePresented(double timestampSeconds)
    {
        _startupFramePresented = true;
        Volatile.Write(ref _presentedVideoPositionSeconds, Math.Max(0, timestampSeconds));
        DiagnosticLog.Write("startup", $"FirstFramePresented=true pts={Math.Max(0, timestampSeconds):0.###}");
    }

    public bool StartPreparedPlayback()
    {
        if (!_decodePriming || !_startupFramePresented || !StartupAudioReady)
        {
            return false;
        }

        // Open the audio gate only after the first video frame has already
        // been rendered.  This prevents the decode thread from starting
        // WASAPI in the small window between IsPlaying=true and the render
        // tick that presents the first frame.
        _decodePriming = false;
        IsPlaying = true;
        Volatile.Write(ref _audioStartGateOpen, 1);
        Volatile.Write(ref _audioNeedsPriming, 0);
        _fallbackClock.Restart();
        DiagnosticLog.Write("startup", $"AudioReady=true buffered_ms={_audioOutput?.BufferedDuration.TotalMilliseconds:0.0}");
        try
        {
            _audioOutput?.Play();
        }
        catch
        {
            Volatile.Write(ref _audioNeedsPriming, 1);
            Volatile.Write(ref _audioStartGateOpen, 0);
            IsPlaying = false;
            throw;
        }
        DiagnosticLog.Write("startup", "ClockStarted=true state=Playing");
        _decodeWake.Set();
        return true;
    }

    public void EnterBuffering()
    {
        if (_reader is null)
        {
            return;
        }

        var position = GetMasterClockSeconds();
        _audioOutput?.Pause();
        _audioOutput?.ClearBuffer();
        _audioOutput?.SetClockPosition(position);
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        _audioSeekTargetSeconds = position;
        _videoSeekTargetSeconds = position;
        _startupFramePresented = false;
        _decodePriming = true;
        IsPlaying = false;
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }

        RequestReaderSeek(position);
        DiagnosticLog.Write("playback", $"Buffering entered at {position:0.###}s");
    }

    public void Pause()
    {
        if (!IsPlaying)
        {
            return;
        }

        // This method is called only after the visual fade has reached black.
        // Freeze both tracks at the audio-master position, then reposition the
        // source reader to that point.  MF may land on an earlier key frame;
        // the scheduler drops those pre-roll frames before presenting them.
        var pausePosition = GetMasterClockSeconds();
        _audioOutput?.Pause();
        _audioOutput?.ClearBuffer();
        _audioOutput?.SetClockPosition(pausePosition);
        Volatile.Write(ref _audioNeedsPriming, 1);
        _audioSeekTargetSeconds = pausePosition;
        _videoSeekTargetSeconds = pausePosition;
        _decodePriming = false;
        _startupFramePresented = false;
        Volatile.Write(ref _positionSeconds, pausePosition);
        Volatile.Write(ref _presentedVideoPositionSeconds, pausePosition);
        IsPlaying = false;
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }

        RequestReaderSeek(pausePosition);
    }

    public void Play()
    {
        if (_reader is not null && !EndOfStream)
        {
            _audioOutput?.SetClockPosition(PositionSeconds);
            Volatile.Write(ref _audioNeedsPriming, 1);
            Volatile.Write(ref _audioStartGateOpen, 1);
            _audioSeekTargetSeconds = PositionSeconds;
            _videoSeekTargetSeconds = PositionSeconds;
            Volatile.Write(ref _presentedVideoPositionSeconds, PositionSeconds);
            _decodePriming = false;
            IsPlaying = true;
            _decodeWake.Set();
        }
    }

    public void PrepareResume()
    {
        if (_reader is null)
        {
            return;
        }

        var resumePosition = PositionSeconds;
        _audioOutput?.Pause();
        _audioOutput?.ClearBuffer();
        _audioOutput?.SetClockPosition(resumePosition);
        _audioSeekTargetSeconds = resumePosition;
        _videoSeekTargetSeconds = resumePosition;
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }
        _decodePriming = true;
        _startupFramePresented = false;
        _startupFirstSampleLogged = false;
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        IsPlaying = false;
        _decodeWake.Set();
    }

    public void SetPosition(double seconds)
    {
        if (_reader is null)
        {
            return;
        }

        seconds = Math.Max(0, seconds);
        _audioOutput?.Pause();
        _audioOutput?.ClearBuffer();
        _audioOutput?.SetClockPosition(seconds);
        Volatile.Write(ref _audioNeedsPriming, 1);
        _audioEndOfStream = false;
        _audioSeekTargetSeconds = seconds;
        _videoSeekTargetSeconds = seconds;
        _decodePriming = false;
        _startupFramePresented = false;
        Volatile.Write(ref _positionSeconds, seconds);
        Volatile.Write(ref _presentedVideoPositionSeconds, seconds);
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }

        RequestReaderSeek(seconds);
        DiagnosticLog.Write("audio", $"seek synchronized position={seconds:0.###}");
    }

    public void SetLooping(bool value) => _loop = value;

    public void SetMuted(bool value)
    {
        IsMuted = value;
        _audioOutput?.SetMuted(value);
    }

    public bool StopAndRelease()
    {
        IsPlaying = false;
        _audioOutput?.Pause();
        if (!StopDecodeThread())
        {
            DiagnosticLog.Write("startup", "decode thread stop timeout; media resources retained for safety");
            return false;
        }

        ReleaseAudio();
        ReleaseReader();
        Width = 0;
        Height = 0;
        Volatile.Write(ref _positionSeconds, 0);
        Volatile.Write(ref _decodedVideoPositionSeconds, 0);
        Volatile.Write(ref _presentedVideoPositionSeconds, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        _sourceFrameRate = 0;
        DurationSeconds = 0;
        _path = null;
        _audioStreamAvailable = false;
        _audioEndOfStream = false;
        _audioSeekTargetSeconds = 0;
        _videoSeekTargetSeconds = 0;
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        _decodePriming = false;
        _startupFramePresented = false;
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAndRelease();
    }

    private void ReleaseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    private void StartDecodeThread()
    {
        _decodeCancellation = new CancellationTokenSource();
        var token = _decodeCancellation.Token;
        _decodeThread = new Thread(() => DecodeLoop(token))
        {
            IsBackground = true,
            Name = "Olivia.MediaFoundation.Decode"
        };
        _decodeThread.Start();
    }

    private bool StopDecodeThread()
    {
        var cancellation = _decodeCancellation;
        if (cancellation is null)
        {
            return true;
        }

        cancellation.Cancel();
        _decodeWake.Set();
        var thread = _decodeThread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                DiagnosticLog.Write("startup", "STARTUP_STALL stage=decode-thread-shutdown elapsed_ms=5000");
                return false;
            }
        }

        _decodeCancellation = null;
        _decodeThread = null;
        cancellation.Dispose();
        return true;
    }

    private void DecodeLoop(CancellationToken cancellationToken)
    {
        const uint coinitMultithreaded = 0x0;
        var comResult = CoInitializeEx(IntPtr.Zero, coinitMultithreaded);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ApplyPendingSeek();

                if ((!IsPlaying && !_decodePriming) || _reader is null)
                {
                    WaitForDecodeWork(cancellationToken);
                    continue;
                }

                if (_loop && _videoEndOfStream && VideoQueueDepth == 0)
                {
                    RestartLoop();
                    continue;
                }

                if (_audioOutput is not null && _audioStreamAvailable
                    && _audioOutput.BufferedDuration < TimeSpan.FromMilliseconds(AudioBufferTargetMilliseconds))
                {
                    PumpAudioSamples(cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (VideoQueueDepth >= MaxVideoQueueDepth)
                {
                    // The render thread signals _decodeWake after consuming a
                    // frame.  Waiting without a polling timeout removes the
                    // old 2 ms wake-up loop that kept laptop CPUs active even
                    // while the bounded queue was already full.
                    WaitForDecodeWork(cancellationToken);
                    continue;
                }

                var queued = ReadAndQueueVideoFrame(cancellationToken);
                if (!queued && VideoQueueDepth == 0)
                {
                    WaitForDecodeWork(cancellationToken, 8);
                }
                TryStartAudioIfReady();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("media-foundation", $"decode thread failed: {ex}");
        }
        finally
        {
            if (comResult >= 0)
            {
                CoUninitialize();
            }
        }
    }

    private void WaitForDecodeWork(CancellationToken cancellationToken, int milliseconds = Timeout.Infinite)
    {
        try
        {
            _decodeWake.Wait(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _decodeWake.Reset();
        }
    }

    private bool ReadAndQueueVideoFrame(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _reader is null
            || (!IsPlaying && !_decodePriming))
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var streamIndex = 0;
        var flags = SourceReaderFlag.None;
        long timestamp100Ns;
        var startupRead = !_startupFirstReadLogged;
        if (startupRead)
        {
            _startupFirstReadLogged = true;
            DiagnosticLog.Write("startup", "FirstReadSample begin");
        }

        try
        {
            // The decode thread is the sole owner of SourceReader I/O.  UI
            // commands only enqueue a seek request, so no lock is held while
            // ReadSample or conversion can block.
            using var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out streamIndex, out flags, out timestamp100Ns);
            if (startupRead)
            {
                DiagnosticLog.Write("startup", $"FirstReadSample result hresult=0 flags={flags} timestamp={timestamp100Ns / 10_000_000d:0.###}");
            }
            if (flags.HasFlag(SourceReaderFlag.EndOfStream))
            {
                lock (_videoQueueGate)
                {
                    _videoEndOfStream = true;
                }

                return false;
            }
            if (flags.HasFlag(SourceReaderFlag.StreamTick)
                || flags.HasFlag(SourceReaderFlag.CurrentMediaTypeChanged)
                || flags.HasFlag(SourceReaderFlag.NativeMediaTypeChanged))
            {
                DiagnosticLog.Write("startup", $"FirstReadSample skipped non-frame flags={flags}");
                return false;
            }

            if (sample is null)
            {
                // Stream ticks/type changes are not valid video frames.
                if (startupRead)
                {
                    DiagnosticLog.Write("startup", $"FirstReadSample result sample=null flags={flags}");
                }
                return false;
            }

            if (flags.HasFlag(SourceReaderFlag.Error))
            {
                throw new InvalidOperationException("Media Foundation Source Reader 返回解码错误。");
            }

            var presentationTime = sample.SampleTime;
            var duration = 0L;
            try
            {
                duration = sample.SampleDuration;
            }
            catch
            {
                // Some sources omit duration; the next PTS remains the
                // authoritative scheduling value.
            }

            var seekTarget100Ns = (long)Math.Max(0, _videoSeekTargetSeconds * 10_000_000d);
            if (seekTarget100Ns > 0
                && presentationTime + Math.Max(0, duration) <= seekTarget100Ns)
            {
                Interlocked.Increment(ref _droppedFrames);
                return false;
            }

            if (seekTarget100Ns > 0)
            {
                _videoSeekTargetSeconds = 0;
            }

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var data, out _, out var currentLength);
            try
            {
                var expectedLength = checked(Width * Height * 4);
                var copyLength = Math.Min(expectedLength, currentLength);
                var pixels = new byte[expectedLength];
                Marshal.Copy(data, pixels, 0, copyLength);
                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    var b0 = copyLength > 0 ? pixels[0] : (byte)0;
                    var b1 = copyLength > 1 ? pixels[1] : (byte)0;
                    var b2 = copyLength > 2 ? pixels[2] : (byte)0;
                    var b3 = copyLength > 3 ? pixels[3] : (byte)0;
                    DiagnosticLog.Write("media-foundation", $"first-frame bytes BGRA={b0:X2},{b1:X2},{b2:X2},{b3:X2} layout=MF_RGB32_memory_B,G,R,A nv12Planes=not-present");
                }

                lock (_videoQueueGate)
                {
                    if (_videoQueue.Count < MaxVideoQueueDepth)
                    {
                        _videoQueue.Enqueue(new VideoFrame(pixels, presentationTime, duration));
                        Interlocked.Increment(ref _decodedFrameCount);
                        Volatile.Write(ref _decodedVideoPositionSeconds, presentationTime / 10_000_000d);
                        if (!_startupFirstSampleLogged)
                        {
                            _startupFirstSampleLogged = true;
                            DiagnosticLog.Write("startup", $"FirstVideoSamplePTS={presentationTime / 10_000_000d:0.###} first_frame_converted=true queue_depth={_videoQueue.Count} startup_ms={_startupStopwatch?.Elapsed.TotalMilliseconds:0.0}");
                        }

                        return true;
                    }
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _lastDecodeMilliseconds = elapsed;
            _maxDecodeMilliseconds = Math.Max(_maxDecodeMilliseconds, elapsed);
            if (startupRead || elapsed >= 100)
            {
                DiagnosticLog.Write("video", $"DecodeTimeMs={elapsed:0.0}");
            }
            if (elapsed >= 500)
            {
                DiagnosticLog.Write("stall", $"VIDEO_STALL stage=ReadSample elapsed_ms={elapsed:0.0} queue_depth={VideoQueueDepth} state={(IsPlaying ? "Playing" : (_decodePriming ? "Preparing" : "Paused"))}");
            }
        }

        return false;
    }

    private void RestartLoop()
    {
        _audioOutput?.Pause();
        _audioOutput?.ClearBuffer();
        _audioOutput?.SetClockPosition(0);
        Volatile.Write(ref _audioNeedsPriming, 1);
        _decodePriming = false;
        _startupFramePresented = false;
        _audioEndOfStream = false;
        _audioSeekTargetSeconds = 0;
        _videoSeekTargetSeconds = 0;
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
            _videoEndOfStream = false;
        }

        FlushAndSeekReader(0);

        Volatile.Write(ref _positionSeconds, 0);
        Volatile.Write(ref _decodedVideoPositionSeconds, 0);
        Volatile.Write(ref _presentedVideoPositionSeconds, 0);
        _fallbackClock.Restart();
    }

    private int GetVideoQueueDepth()
    {
        lock (_videoQueueGate)
        {
            return _videoQueue.Count;
        }
    }

    private void ClearVideoQueue()
    {
        lock (_videoQueueGate)
        {
            _videoQueue.Clear();
        }
    }

    private void RequestReaderSeek(double seconds)
    {
        lock (_commandGate)
        {
            _pendingSeekSeconds = Math.Max(0, seconds);
            _seekRequested = true;
        }

        _decodeWake.Set();
    }

    private void ApplyPendingSeek()
    {
        double seconds;
        lock (_commandGate)
        {
            if (!_seekRequested)
            {
                return;
            }

            seconds = _pendingSeekSeconds;
            _seekRequested = false;
        }

        FlushAndSeekReader(seconds);
        DiagnosticLog.Write("video", $"Seek applied on decode thread position={seconds:0.###}");
    }

    private void FlushAndSeekReader(double seconds)
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            _reader.Flush(SourceReaderIndex.AllStreams);
        }
        catch
        {
            try { _reader.Flush(SourceReaderIndex.FirstVideoStream); } catch { }
            try { _reader.Flush(SourceReaderIndex.FirstAudioStream); } catch { }
        }

        var timestamp = checked((long)(Math.Max(0, seconds) * 10_000_000d));
        _reader.SetCurrentPosition(timestamp);
    }

    private void ConfigureAudioStream()
    {
        _audioStreamAvailable = false;
        _audioEndOfStream = false;
        _audioOutput = null;

        if (_reader is null)
        {
            DiagnosticLog.Write("audio", "Stream found = false");
            DiagnosticLog.Write("audio", "Audio stream disabled / not selected");
            return;
        }

        IMFMediaType? nativeType = null;
        try
        {
            nativeType = _reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
            _audioStreamAvailable = true;
            var nativeSubtype = TryGetGuid(nativeType, MediaTypeAttributeKeys.Subtype);
            DiagnosticLog.Write("audio", $"Stream found = true Codec={DescribeAudioSubtype(nativeSubtype)}");

            _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using var outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);

            // Supplying the native rate/channel count makes the PCM request
            // deterministic for AAC/MP3 files while keeping MF responsible
            // for the actual codec decode.
            var sampleRate = TryGetUInt32(nativeType, MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000);
            var channels = TryGetUInt32(nativeType, MediaTypeAttributeKeys.AudioNumChannels, 2);
            var bits = TryGetUInt32(nativeType, MediaTypeAttributeKeys.AudioBitsPerSample, 16);
            if (bits is 0 or > 32)
            {
                bits = 16;
            }

            outputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, sampleRate);
            outputType.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
            outputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, bits);
            outputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, checked(channels * bits / 8));
            outputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, checked(sampleRate * channels * bits / 8));

            _reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, outputType);
            using var currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            sampleRate = TryGetUInt32(currentType, MediaTypeAttributeKeys.AudioSamplesPerSecond, sampleRate);
            channels = TryGetUInt32(currentType, MediaTypeAttributeKeys.AudioNumChannels, channels);
            bits = TryGetUInt32(currentType, MediaTypeAttributeKeys.AudioBitsPerSample, bits);
            var currentSubtype = TryGetGuid(currentType, MediaTypeAttributeKeys.Subtype);
            var isFloat = currentSubtype == AudioFormatGuids.Float;
            if (isFloat)
            {
                bits = 32;
            }

            _audioOutput = new WasapiAudioOutput((int)sampleRate, (int)channels, (int)bits, isFloat);
            _audioOutput.SetMuted(IsMuted);
            DiagnosticLog.Write("audio", $"SampleRate={sampleRate} Channels={channels} BitsPerSample={bits} OutputFormat={DescribeAudioSubtype(currentSubtype)}");
        }
        catch (Exception ex)
        {
            _audioStreamAvailable = false;
            ReleaseAudio();
            try
            {
                _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, false);
            }
            catch
            {
                // The stream may not have been selected if native discovery failed.
            }
            DiagnosticLog.Write("audio", $"Stream found = {_audioStreamAvailable.ToString().ToLowerInvariant()}");
            DiagnosticLog.Write("audio", $"Audio stream disabled / not selected: {ex.Message}");
        }
        finally
        {
            nativeType?.Dispose();
        }
    }

    private void PumpAudioSamples(CancellationToken cancellationToken)
    {
        if (!_audioStreamAvailable || _audioOutput is null || (!IsPlaying && !_decodePriming) || _audioEndOfStream)
        {
            return;
        }

        // Keep a small realtime cushion without decoding audio faster than it
        // can be consumed by WASAPI.  This also bounds memory use for long
        // videos while allowing the render timer to run at 60 Hz.
        try
        {
            var attempts = 0;
            while (_audioOutput.BufferedDuration < TimeSpan.FromMilliseconds(AudioBufferTargetMilliseconds)
                && attempts++ < 32 && !cancellationToken.IsCancellationRequested)
            {
                var reader = _reader;
                if (reader is null || (!IsPlaying && !_decodePriming))
                {
                    return;
                }

                var streamIndex = 0;
                var flags = SourceReaderFlag.None;
                long timestamp100Ns;
                using var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out streamIndex, out flags, out timestamp100Ns);
                ProcessAudioSample(sample, flags, timestamp100Ns);
            }

            TryStartAudioIfReady();
        }
        catch (Exception ex)
        {
            // Audio failure must not tear down the already working video path.
            _audioEndOfStream = true;
            DiagnosticLog.Write("audio", $"audio pump failed; video continues: {ex.Message}");
        }
    }

    private void ProcessAudioSample(IMFSample? sample, SourceReaderFlag flags, long timestamp100Ns)
    {
        if (flags.HasFlag(SourceReaderFlag.EndOfStream))
        {
            if (!_audioEndOfStream)
            {
                DiagnosticLog.Write("audio", "EOS reached");
            }

            _audioEndOfStream = true;
            return;
        }

        if (flags.HasFlag(SourceReaderFlag.StreamTick)
            || flags.HasFlag(SourceReaderFlag.CurrentMediaTypeChanged)
            || flags.HasFlag(SourceReaderFlag.NativeMediaTypeChanged))
        {
            return;
        }

        if (sample is null)
        {
            return;
        }

        if (flags.HasFlag(SourceReaderFlag.Error))
        {
            DiagnosticLog.Write("audio", "audio sample decode returned an error");
            _audioEndOfStream = true;
            return;
        }

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var data, out _, out var currentLength);
        try
        {
            if (currentLength > 0 && _audioOutput is not null)
            {
                var bytes = new byte[currentLength];
                Marshal.Copy(data, bytes, 0, currentLength);
                var offset = GetAudioTrimOffset(sample, timestamp100Ns, currentLength, _audioOutput.WaveFormat.BlockAlign);
                if (offset < bytes.Length)
                {
                    _audioOutput.AddSamples(bytes, offset, bytes.Length - offset);
                }
                if (!_audioFirstSampleLogged)
                {
                    _audioFirstSampleLogged = true;
                    DiagnosticLog.Write("audio", $"first PCM sample timestamp={timestamp100Ns / 10_000_000d:0.###}s bytes={currentLength}");
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private bool CanStartAudioClock()
    {
        if (_audioOutput is null)
        {
            return false;
        }

        lock (_videoQueueGate)
        {
            return _videoQueue.Count > 0 && _videoSeekTargetSeconds <= 0;
        }
    }

    private void TryStartAudioIfReady()
    {
        if (Volatile.Read(ref _audioStartGateOpen) == 0
            || Volatile.Read(ref _audioNeedsPriming) == 0
            || _audioOutput?.BufferedDuration <= TimeSpan.Zero
            || !IsPlaying
            || !CanStartAudioClock())
        {
            return;
        }

        // Claim the start before calling into WASAPI so a render tick and the
        // decode thread cannot start the same audio session twice.
        if (Interlocked.CompareExchange(ref _audioNeedsPriming, 0, 1) != 1)
        {
            return;
        }

        var audioOutput = _audioOutput;
        if (audioOutput is null)
        {
            Volatile.Write(ref _audioNeedsPriming, 1);
            return;
        }

        try
        {
            audioOutput.Play();
        }
        catch
        {
            Volatile.Write(ref _audioNeedsPriming, 1);
            throw;
        }
    }

    private int GetAudioTrimOffset(IMFSample sample, long timestamp100Ns, int length, int blockAlign)
    {
        var target100Ns = (long)Math.Max(0, _audioSeekTargetSeconds * 10_000_000d);
        if (target100Ns <= 0 || timestamp100Ns >= target100Ns)
        {
            _audioSeekTargetSeconds = 0;
            return 0;
        }

        long duration100Ns;
        try
        {
            duration100Ns = sample.SampleDuration;
        }
        catch
        {
            duration100Ns = 0;
        }

        if (duration100Ns <= 0 || timestamp100Ns + duration100Ns <= target100Ns)
        {
            return length;
        }

        var ratio = (target100Ns - timestamp100Ns) / (double)duration100Ns;
        var offset = (int)Math.Clamp(Math.Round(length * ratio), 0, length);
        offset -= offset % Math.Max(1, blockAlign);
        _audioSeekTargetSeconds = 0;
        return offset;
    }

    private void ReleaseAudio()
    {
        _audioOutput?.Dispose();
        _audioOutput = null;
        _audioStreamAvailable = false;
    }

    private static uint TryGetUInt32(IMFMediaType type, Guid key, uint fallback)
    {
        try
        {
            var value = type.GetUInt32(key);
            return value == 0 ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string DescribeAudioSubtype(Guid subtype)
    {
        if (subtype == AudioFormatGuids.Pcm)
        {
            return "PCM";
        }

        if (subtype == AudioFormatGuids.Float)
        {
            return "Float PCM";
        }

        if (subtype == AudioFormatGuids.Aac)
        {
            return "AAC";
        }

        if (subtype == AudioFormatGuids.Mp3)
        {
            return "MP3";
        }

        return subtype == Guid.Empty ? "unset" : subtype.ToString("D");
    }

    public AvSyncDiagnostics GetAvSyncDiagnostics()
    {
        var master = GetMasterClockSeconds();
        var audio = AudioPositionSeconds;
        var presented = Volatile.Read(ref _presentedVideoPositionSeconds);
        return new AvSyncDiagnostics(
            master,
            audio,
            Volatile.Read(ref _decodedVideoPositionSeconds),
            presented,
            (presented - master) * 1000,
            VideoQueueDepth,
            Interlocked.Read(ref _droppedFrames),
            _lastDecodeMilliseconds,
            _maxDecodeMilliseconds);
    }

    public VideoPerformanceCounters GetPerformanceCounters(long renderTicks, long presentCalls)
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.001, (now - _lastCounterTimeUtc).TotalSeconds);
        var decoded = Interlocked.Read(ref _decodedFrameCount);
        var presented = Interlocked.Read(ref _presentedFrameCount);
        var decodedPerSecond = (decoded - _lastCounterDecoded) / elapsed;
        var presentedPerSecond = (presented - _lastCounterPresented) / elapsed;
        var renderTicksPerSecond = (renderTicks - _lastCounterRenderTicks) / elapsed;
        var presentCallsPerSecond = (presentCalls - _lastCounterPresentCalls) / elapsed;
        _lastCounterDecoded = decoded;
        _lastCounterPresented = presented;
        _lastCounterRenderTicks = renderTicks;
        _lastCounterPresentCalls = presentCalls;
        _lastCounterTimeUtc = now;
        return new VideoPerformanceCounters(
            _sourceFrameRate,
            Math.Max(0, decodedPerSecond),
            Math.Max(0, presentedPerSecond),
            Math.Max(0, renderTicksPerSecond),
            Math.Max(0, presentCallsPerSecond));
    }

    private double GetMasterClockSeconds()
    {
        if (_audioOutput?.IsClockStarted == true)
        {
            var audioPosition = _audioOutput.PlaybackPositionSeconds;
            Volatile.Write(ref _positionSeconds, audioPosition);
            return audioPosition;
        }

        if (HasAudio)
        {
            // Keep media time at the priming position until the device clock
            // actually starts; a fallback wall clock here would make the
            // first video frame appear late and then jump back to zero.
            return Volatile.Read(ref _positionSeconds);
        }

        if (IsPlaying && _fallbackClock.IsRunning)
        {
            var fallbackPosition = Math.Max(Volatile.Read(ref _positionSeconds), _fallbackClock.Elapsed.TotalSeconds);
            Volatile.Write(ref _positionSeconds, fallbackPosition);
            return fallbackPosition;
        }

        return Volatile.Read(ref _positionSeconds);
    }

    private static void EnsureMediaFoundation()
    {
        if (_mediaFoundationStarted)
        {
            return;
        }

        lock (StartupGate)
        {
            if (_mediaFoundationStarted)
            {
                return;
            }

            MediaFactory.MFStartup(false).CheckError();
            _mediaFoundationStarted = true;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static string DescribeColorMetadata(IMFMediaType type)
    {
        var subtype = TryGetGuid(type, MediaTypeAttributeKeys.Subtype);
        var subtypeName = subtype switch
        {
            var value when value == VideoFormatGuids.Rgb32 => "RGB32",
            var value when value == VideoFormatGuids.Argb32 => "ARGB32",
            var value when value == Nv12Subtype => "NV12",
            var value when value == Guid.Empty => "unset",
            _ => subtype.ToString("D")
        };
        return $"subtype={subtypeName} yuvMatrix={TryGetEnum<VideoTransferMatrix>(type, MediaTypeAttributeKeys.YuvMatrix)} nominalRange={TryGetEnum<NominalRange>(type, MediaTypeAttributeKeys.VideoNominalRange)} primaries={TryGetEnum<VideoPrimaries>(type, MediaTypeAttributeKeys.VideoPrimaries)} transfer={TryGetEnum<VideoTransferFunction>(type, MediaTypeAttributeKeys.TransferFunction)}";
    }

    private static double TryGetFrameRate(IMFMediaType type)
    {
        try
        {
            var packed = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            var numerator = (uint)(packed >> 32);
            var denominator = (uint)(packed & 0xFFFF_FFFF);
            return denominator == 0 ? 0 : numerator / (double)denominator;
        }
        catch
        {
            return 0;
        }
    }

    private static Guid TryGetGuid(IMFMediaType type, Guid key)
    {
        try
        {
            return type.GetGUID(key);
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static string TryGetEnum<T>(IMFMediaType type, Guid key) where T : struct, Enum
    {
        try
        {
            var raw = type.GetUInt32(key);
            return Enum.GetName(typeof(T), raw) ?? $"0x{raw:X8}";
        }
        catch
        {
            return "unset";
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaFoundationVideoDecoder));
        }
    }
}
