using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
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
    double PresentCallsPerSecond,
    double ReadSampleAverageMilliseconds,
    double ReadSampleMaxMilliseconds);

/// <summary>
/// Media Foundation Source Reader 解码器。
/// Auto 模式优先输出绑定 D3D11 设备的 NV12 GPU Surface，失败时保留 RGB32 CPU 回退，避免 WMP/EVR 创建独立全屏 HWND。
/// </summary>
internal sealed class MediaFoundationVideoDecoder : IDisposable
{
    private const int MaxVideoQueueDepth = 4;
    private const int MinimumBufferedVideoFrames = 3;
    private const double EarlyThresholdSeconds = 0.004;
    private const double LateThresholdSeconds = 0.050;
    private const double AudioBufferTargetMilliseconds = 150;
    private static readonly object StartupGate = new();
    private static bool _mediaFoundationStarted;
    private static bool ForceSoftwareDecoder => false;
    // The release playback path is intentionally fixed to the proven RGB32
    // CPU-buffer pipeline.  The experimental D3D-manager/NV12 branches remain
    // in the source for historical diagnostics but cannot be enabled by an
    // inherited environment variable or an accidental launch configuration.
    public static bool GpuVideoPipelineEnabled => false;
    private static bool HardwareDecodeExperimentEnabled => false;
    private readonly object _commandGate = new();
    private readonly object _videoQueueGate = new();
    private readonly Queue<VideoFrame> _videoQueue = new();
    private readonly ManualResetEventSlim _decodeWake = new(false);
    private IMFSourceReader? _reader;
    private IMFDXGIDeviceManager? _dxgiDeviceManager;
    private Vortice.Direct3D11.ID3D11Device? _renderDevice;
    private Guid _outputSubtype;
    private bool _gpuSurfacePipelineActive;
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
    private bool _videoBufferProbeLogged;
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
    private double _totalDecodeMilliseconds;
    private DateTime _lastDecodeUtc = DateTime.MinValue;
    private DateTime _lastAudioSubmitUtc = DateTime.MinValue;
    private long _lastCounterDecoded;
    private double _lastCounterDecodeMilliseconds;
    private double _sourceFrameRate;
    private long _decodedFrameCount;
    private long _presentedFrameCount;
    private long _lastCounterPresented;
    private long _lastCounterRenderTicks;
    private long _lastCounterPresentCalls;
    private DateTime _lastCounterTimeUtc = DateTime.UtcNow;
    private bool _seekRequested;
    private double _pendingSeekSeconds;
    private long _currentDecodeFrameId;

    private static readonly Guid Nv12Subtype = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid P010Subtype = new("30313050-0000-0010-8000-00AA00389B71");
    private static readonly Guid Yuy2Subtype = new("32595559-0000-0010-8000-00AA00389B71");
    private static readonly Guid UyvySubtype = new("59565955-0000-0010-8000-00AA00389B71");
    private static readonly Guid Yv12Subtype = new("32315659-0000-0010-8000-00AA00389B71");
    private static readonly Guid IyuvSubtype = new("56555949-0000-0010-8000-00AA00389B71");
    private static readonly Guid YvyuSubtype = new("55595659-0000-0010-8000-00AA00389B71");
    private static readonly Guid I420Subtype = new("30323449-0000-0010-8000-00AA00389B71");

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
    public bool StartupAudioReady => !HasAudio
        || _audioOutput?.BufferedDuration >= TimeSpan.FromMilliseconds(AudioBufferTargetMilliseconds)
        || (_audioEndOfStream && _audioOutput?.BufferedDuration > TimeSpan.Zero);
    public TimeSpan AudioBufferedDuration => _audioOutput?.BufferedDuration ?? TimeSpan.Zero;
    public double LastDecodeAgeMilliseconds => _lastDecodeUtc == DateTime.MinValue
        ? double.PositiveInfinity
        : Math.Max(0, (DateTime.UtcNow - _lastDecodeUtc).TotalMilliseconds);
    public double LastAudioSubmitAgeMilliseconds => _lastAudioSubmitUtc == DateTime.MinValue
        ? double.PositiveInfinity
        : Math.Max(0, (DateTime.UtcNow - _lastAudioSubmitUtc).TotalMilliseconds);
    public bool StartupFrameReady
    {
        get
        {
            lock (_videoQueueGate)
            {
                return _videoQueue.Count >= MinimumBufferedVideoFrames && _videoSeekTargetSeconds <= 0;
            }
        }
    }
    public bool StartupFramePresented => _startupFramePresented;
    public int VideoQueueDepth => GetVideoQueueDepth();
    public double MasterClockSeconds => GetMasterClockSeconds();
    public double AudioPositionSeconds => _audioOutput?.PlaybackPositionSeconds ?? 0;
    public double SourceFrameRate => _sourceFrameRate;
    public event EventHandler? FrameAvailable;

    private int _isPlaying;

    private readonly record struct VideoFrame(byte[]? Pixels, Vortice.Direct3D11.ID3D11Texture2D? Texture, uint SubresourceIndex, long PresentationTime100Ns, long Duration100Ns);

    public bool UsesGpuSurface => Volatile.Read(ref _gpuSurfacePipelineActive);

    public MediaFoundationVideoDecoder()
    {
        GpuPipelineDiagnostics.SetSnapshotProvider(() => $"VideoQueueDepth={VideoQueueDepth} LastDecodeAgoMs={LastDecodeAgeMilliseconds:0.0} LastAudioSubmitAgoMs={LastAudioSubmitAgeMilliseconds:0.0}");
    }

    public void Open(string path, bool loop, Vortice.Direct3D11.ID3D11Device? renderDevice = null)
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
        ReleaseDxgiDeviceManager();
        _renderDevice = renderDevice;
        _gpuSurfacePipelineActive = false;
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
        _videoBufferProbeLogged = false;
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
        _lastCounterDecodeMilliseconds = 0;
        _lastCounterPresented = 0;
        _lastCounterRenderTicks = 0;
        _lastCounterPresentCalls = 0;
        _lastCounterTimeUtc = DateTime.UtcNow;
        _lastDecodeMilliseconds = 0;
        _maxDecodeMilliseconds = 0;
        _totalDecodeMilliseconds = 0;
        _lastDecodeUtc = DateTime.MinValue;
        _lastAudioSubmitUtc = DateTime.MinValue;
        _fallbackClock.Restart();
        ClearVideoQueue();
        DiagnosticLog.Write("startup", $"OpenSource begin file={path}");

        using var attributes = MediaFactory.MFCreateAttributes(6);
        var dxgiManagerAttached = false;
        var attachDxgiManager = (GpuVideoPipelineEnabled || HardwareDecodeExperimentEnabled)
            && !ForceSoftwareDecoder && renderDevice is not null;
        if (attachDxgiManager)
        {
            try
            {
                _dxgiDeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                _dxgiDeviceManager.ResetDevice(renderDevice);
                attributes.Set(SourceReaderAttributeKeys.D3DManager, _dxgiDeviceManager);
                dxgiManagerAttached = true;
                DiagnosticLog.Write("DECODER_INFO", $"MFCreateDXGIDeviceManager=true ResetToken={_dxgiDeviceManager.ResetToken} SourceReaderD3DManagerAttached=true");
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("DECODER_INFO", $"MFCreateDXGIDeviceManager=false type={exception.GetType().Name} message={exception.Message}");
                ReleaseDxgiDeviceManager();
            }
        }

        // The stable path keeps MF video processing enabled.  The isolated
        // D3D-manager experiment must disable it because Microsoft documents
        // that ENABLE_VIDEO_PROCESSING and D3D_MANAGER cannot be combined.
        // It still requests RGB32 below, so the proven queue/renderer path is
        // unchanged and the experiment never enables the NV12 renderer.
        var hardwareExperiment = HardwareDecodeExperimentEnabled;
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing,
            GpuVideoPipelineEnabled ? false : !hardwareExperiment);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, !ForceSoftwareDecoder);
        if (hardwareExperiment)
        {
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, false);
        }
        else if (ForceSoftwareDecoder)
        {
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, true);
        }

        DiagnosticLog.Write("DECODER_INFO", $"DecoderMode={(ForceSoftwareDecoder ? "ForceSoftware" : "Auto")} GpuVideoPipelineEnabled={GpuVideoPipelineEnabled} HardwareDecodeExperimentEnabled={hardwareExperiment} HardwareTransformsRequested={!ForceSoftwareDecoder} EnableVideoProcessing={(GpuVideoPipelineEnabled ? false : !hardwareExperiment)} AdvancedVideoProcessing={hardwareExperiment} DisableConverters=false(not-set) DisableDxva={(hardwareExperiment ? false : ForceSoftwareDecoder)} SourceReaderD3DManagerAttached={dxgiManagerAttached}");
        _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        DiagnosticLog.Write("startup", "VideoStreamSelected=true");
        ConfigureAudioStream();
        DiagnosticLog.Write("startup", $"AudioStreamSelected={HasAudio}");

        var inputSubtype = Guid.Empty;
        IMFMediaType? nativeType = null;
        try
        {
            nativeType = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            inputSubtype = TryGetGuid(nativeType, MediaTypeAttributeKeys.Subtype);
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
        _outputSubtype = GpuVideoPipelineEnabled && dxgiManagerAttached ? Nv12Subtype : VideoFormatGuids.Rgb32;
        outputType.Set(MediaTypeAttributeKeys.Subtype, _outputSubtype);
        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
        DiagnosticLog.Write("DECODER_INFO", $"RGB32OutputPreserved={(_outputSubtype == VideoFormatGuids.Rgb32)} HardwareDecodeExperimentEnabled={HardwareDecodeExperimentEnabled}");

        using var currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var packedSize = currentType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        MediaFactory.UnpackSize(packedSize, out var width, out var height);
        Width = checked((int)width);
        Height = checked((int)height);
        var decoderCandidates = LogDecoderCandidates(inputSubtype);
        _sourceFrameRate = TryGetFrameRate(currentType);
        LogVideoDecoderChain(_reader, inputSubtype, currentType, decoderCandidates, dxgiManagerAttached);
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
        DiagnosticLog.Write("media-foundation", $"output {DescribeColorMetadata(currentType)} dxgiUpload=B8G8R8A8_UNORM yuvConversion={(currentType.GetGUID(MediaTypeAttributeKeys.Subtype) == Nv12Subtype ? "D3D11 VideoProcessor" : "MediaFoundation")}");
        var hardwareMftStatus = ForceSoftwareDecoder ? "disabled" : "not-confirmed-until-transform-probe";
        DiagnosticLog.Write("media-foundation", $"opened path={path} size={Width}x{Height} output={DescribeVideoSubtype(_outputSubtype)} loop={_loop} source_fps={_sourceFrameRate:0.###} hardware_mft={hardwareMftStatus} gpu_surface_requested={dxgiManagerAttached}");
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
        while (true)
        {
            VideoFrame frame;
            var dropFrame = false;
            lock (_videoQueueGate)
            {
                if (_videoQueue.Count == 0)
                {
                    return false;
                }

                frame = _videoQueue.Peek();
                var frameSeconds = frame.PresentationTime100Ns / 10_000_000d;
                var deltaSeconds = frameSeconds - masterClock;
                if (deltaSeconds < -LateThresholdSeconds)
                {
                    _videoQueue.Dequeue();
                    dropFrame = true;
                }
                else if (HasAudio && _audioOutput?.IsClockStarted == true && deltaSeconds > EarlyThresholdSeconds)
                {
                    return false;
                }
                else
                {
                    _videoQueue.Dequeue();
                    pixels = frame.Pixels;
                    timestamp100Ns = frame.PresentationTime100Ns;
                    Interlocked.Increment(ref _presentedFrameCount);
                    Volatile.Write(ref _positionSeconds, frameSeconds);
                    Volatile.Write(ref _presentedVideoPositionSeconds, frameSeconds);
                }
            }

            // Texture/COM release is deliberately outside _videoQueueGate.  A
            // decoder surface may block while being returned to MF's pool.
            if (dropFrame)
            {
                ReleaseVideoFrame(frame);
                Interlocked.Increment(ref _droppedFrames);
                _decodeWake.Set();
                continue;
            }

            _decodeWake.Set();
            if (pixels is null)
            {
                ReleaseVideoFrame(frame);
                return false;
            }

            return true;
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

        VideoFrame frame;
        lock (_videoQueueGate)
        {
            if ((_videoQueue.Count < MinimumBufferedVideoFrames && !_videoEndOfStream) || _videoQueue.Count == 0 || _videoSeekTargetSeconds > 0)
            {
                return false;
            }

            frame = _videoQueue.Dequeue();
            pixels = frame.Pixels;
            timestamp100Ns = frame.PresentationTime100Ns;
            Interlocked.Increment(ref _presentedFrameCount);
            Volatile.Write(ref _decodedVideoPositionSeconds, frame.PresentationTime100Ns / 10_000_000d);
        }

        _decodeWake.Set();
        if (pixels is null)
        {
            ReleaseVideoFrame(frame);
            return false;
        }

        return true;
    }

    public void ReleaseFrame(byte[] pixels)
    {
        ReturnPixelBuffer(pixels);
    }

    public bool TryReadGpuFrame(out Vortice.Direct3D11.ID3D11Texture2D? texture, out uint subresourceIndex, out long timestamp100Ns)
    {
        ThrowIfDisposed();
        texture = null;
        subresourceIndex = 0;
        timestamp100Ns = 0;
        if (!IsPlaying || !UsesGpuSurface)
        {
            return false;
        }

        if (HasAudio && _audioOutput?.IsClockStarted != true)
        {
            return false;
        }

        var masterClock = GetMasterClockSeconds();
        while (true)
        {
            VideoFrame frame;
            var dropFrame = false;
            lock (_videoQueueGate)
            {
                if (_videoQueue.Count == 0)
                {
                    return false;
                }

                frame = _videoQueue.Peek();
                var frameSeconds = frame.PresentationTime100Ns / 10_000_000d;
                var deltaSeconds = frameSeconds - masterClock;
                if (deltaSeconds < -LateThresholdSeconds)
                {
                    _videoQueue.Dequeue();
                    dropFrame = true;
                }
                else if (deltaSeconds > EarlyThresholdSeconds)
                {
                    return false;
                }
                else
                {
                    _videoQueue.Dequeue();
                    texture = frame.Texture;
                    subresourceIndex = frame.SubresourceIndex;
                    timestamp100Ns = frame.PresentationTime100Ns;
                    Interlocked.Increment(ref _presentedFrameCount);
                    Volatile.Write(ref _positionSeconds, frameSeconds);
                    Volatile.Write(ref _presentedVideoPositionSeconds, frameSeconds);
                }
            }

            if (dropFrame)
            {
                ReleaseVideoFrame(frame);
                Interlocked.Increment(ref _droppedFrames);
                _decodeWake.Set();
                continue;
            }

            _decodeWake.Set();
            if (texture is null)
            {
                ReleaseVideoFrame(frame);
                return false;
            }

            return true;
        }
    }

    public bool TryReadPreparedGpuFrame(out Vortice.Direct3D11.ID3D11Texture2D? texture, out uint subresourceIndex, out long timestamp100Ns)
    {
        ThrowIfDisposed();
        texture = null;
        subresourceIndex = 0;
        timestamp100Ns = 0;
        if (!_decodePriming || !UsesGpuSurface)
        {
            return false;
        }

        VideoFrame frame;
        lock (_videoQueueGate)
        {
            if ((_videoQueue.Count < MinimumBufferedVideoFrames && !_videoEndOfStream) || _videoQueue.Count == 0 || _videoSeekTargetSeconds > 0)
            {
                return false;
            }

            frame = _videoQueue.Dequeue();
            texture = frame.Texture;
            subresourceIndex = frame.SubresourceIndex;
            timestamp100Ns = frame.PresentationTime100Ns;
            Interlocked.Increment(ref _presentedFrameCount);
            Volatile.Write(ref _decodedVideoPositionSeconds, frame.PresentationTime100Ns / 10_000_000d);
        }

        _decodeWake.Set();
        if (texture is null)
        {
            ReleaseVideoFrame(frame);
            return false;
        }

        return true;
    }

    public void ReleaseGpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture)
    {
        ReleaseGpuTexture(texture);
    }

    public void MarkFirstFramePresented(double timestampSeconds)
    {
        _startupFramePresented = true;
        Volatile.Write(ref _presentedVideoPositionSeconds, Math.Max(0, timestampSeconds));
        DiagnosticLog.Write("startup", $"FirstFramePresented=true pts={Math.Max(0, timestampSeconds):0.###}");
    }

    public bool StartPreparedPlayback()
    {
        if (!_decodePriming || !_startupFramePresented || !StartupAudioReady || !HasMinimumBufferedFramesAfterPrime())
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

    private bool HasMinimumBufferedFramesAfterPrime()
    {
        lock (_videoQueueGate)
        {
            // The first prepared frame has already been rendered and removed
            // from the queue.  Keep at least two more frames queued so startup
            // and buffering recovery begin with three frames of total cushion.
            return (_videoEndOfStream && _videoQueue.Count > 0)
                || _videoQueue.Count >= MinimumBufferedVideoFrames - 1;
        }
    }

    /// <summary>
    /// Freeze consumption while retaining the current frame queue.  The
    /// decode thread stays in priming mode and fills the queue until the
    /// scheduler can resume both tracks together.
    /// </summary>
    public bool BeginBuffering()
    {
        if (_reader is null || !IsPlaying || _decodePriming)
        {
            return false;
        }

        var position = GetMasterClockSeconds();
        _audioOutput?.Pause();
        _audioOutput?.SetClockPosition(position);
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        Volatile.Write(ref _positionSeconds, position);
        _decodePriming = true;
        IsPlaying = false;
        _decodeWake.Set();
        DiagnosticLog.Write("playback", $"Buffering entered without flush at {position:0.###}s queue_depth={VideoQueueDepth} audio_buffered_ms={AudioBufferedDuration.TotalMilliseconds:0.0}");
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
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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
        ReleaseDxgiDeviceManager();
        Width = 0;
        Height = 0;
        Volatile.Write(ref _positionSeconds, 0);
        Volatile.Write(ref _decodedVideoPositionSeconds, 0);
        Volatile.Write(ref _presentedVideoPositionSeconds, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        _sourceFrameRate = 0;
        _lastCounterDecoded = 0;
        _lastCounterDecodeMilliseconds = 0;
        _lastCounterPresented = 0;
        _lastCounterRenderTicks = 0;
        _lastCounterPresentCalls = 0;
        _lastCounterTimeUtc = DateTime.UtcNow;
        _lastDecodeMilliseconds = 0;
        _maxDecodeMilliseconds = 0;
        _totalDecodeMilliseconds = 0;
        DurationSeconds = 0;
        _path = null;
        _audioStreamAvailable = false;
        _audioEndOfStream = false;
        _audioSeekTargetSeconds = 0;
        _videoSeekTargetSeconds = 0;
        _outputSubtype = Guid.Empty;
        _gpuSurfacePipelineActive = false;
        Volatile.Write(ref _audioNeedsPriming, 1);
        Volatile.Write(ref _audioStartGateOpen, 0);
        _decodePriming = false;
        _startupFramePresented = false;
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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

    private void ReleaseDxgiDeviceManager()
    {
        _dxgiDeviceManager?.Dispose();
        _dxgiDeviceManager = null;
        _renderDevice = null;
    }

    private void StartDecodeThread()
    {
        _decodeCancellation = new CancellationTokenSource();
        var token = _decodeCancellation.Token;
        _decodeThread = new Thread(() => DecodeLoop(token))
        {
            IsBackground = true,
            Name = "Olivia.VideoDecode"
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
        ThreadCpuDiagnostics.RegisterCurrentThread("Olivia.VideoDecode");
        GpuPipelineDiagnostics.RegisterThread("Olivia.VideoDecode", "idle");
        var comResult = CoInitializeEx(IntPtr.Zero, coinitMultithreaded);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ThreadCpuDiagnostics.MarkWakeup("Olivia.VideoDecode");
                GpuPipelineDiagnostics.MarkProgress("Olivia.VideoDecode", "loop");
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
                    ThreadCpuDiagnostics.MarkWakeup("Olivia.AudioDecode");
                    using (ThreadCpuDiagnostics.StartActivity("Olivia.AudioDecode"))
                    {
                        PumpAudioSamples(cancellationToken);
                    }
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

                bool queued;
                using (ThreadCpuDiagnostics.StartActivity("Olivia.VideoDecode"))
                {
                    queued = ReadAndQueueVideoFrame(cancellationToken);
                }
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
            using var waitStage = GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "DecodeWait", _currentDecodeFrameId);
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
        var reader = _reader;
        if (cancellationToken.IsCancellationRequested || reader is null
            || (!IsPlaying && !_decodePriming))
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var frameId = GpuPipelineDiagnostics.NextFrameId();
        _currentDecodeFrameId = frameId;
        GpuPipelineDiagnostics.MarkProgress("Olivia.VideoDecode", "ReadSample", frameId);
        var streamIndex = 0;
        var flags = SourceReaderFlag.None;
        long timestamp100Ns;
        var sampleTracked = false;
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
            IMFSample? sample;
            using (var readStage = GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "ReadSample", frameId))
            {
                sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out streamIndex, out flags, out timestamp100Ns);
                readStage.SetPresentationTime(timestamp100Ns);
            }
            using (sample)
            {
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

            GpuPipelineDiagnostics.DecoderSampleAcquired();
            sampleTracked = true;

            if (flags.HasFlag(SourceReaderFlag.Error))
            {
                throw new InvalidOperationException("Media Foundation Source Reader 返回解码错误。");
            }

            if (!_videoBufferProbeLogged)
            {
                _videoBufferProbeLogged = true;
                ProbeVideoSampleBuffer(sample);
            }

            var presentationTime = sample.SampleTime;
            GpuPipelineDiagnostics.MarkProgress("Olivia.VideoDecode", "SampleReceived", frameId);
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

            if (_outputSubtype == Nv12Subtype && _dxgiDeviceManager is not null)
            {
                if (TryGetGpuSurface(sample, frameId, out var texture, out var subresourceIndex))
                {
                    var queuedGpu = false;
                    var signalFrameAvailable = false;
                    lock (_videoQueueGate)
                    {
                        if (_videoQueue.Count < MaxVideoQueueDepth)
                        {
                            signalFrameAvailable = _videoQueue.Count == 0;
                            _gpuSurfacePipelineActive = true;
                            _videoQueue.Enqueue(new VideoFrame(null, texture, subresourceIndex, presentationTime, duration));
                            Interlocked.Increment(ref _decodedFrameCount);
                            Volatile.Write(ref _decodedVideoPositionSeconds, presentationTime / 10_000_000d);
                            if (!_startupFirstSampleLogged)
                            {
                                _startupFirstSampleLogged = true;
                                DiagnosticLog.Write("startup", $"FirstVideoSamplePTS={presentationTime / 10_000_000d:0.###} first_frame_gpu_surface=true queue_depth={_videoQueue.Count} startup_ms={_startupStopwatch?.Elapsed.TotalMilliseconds:0.0}");
                            }
                            queuedGpu = true;
                        }
                    }

                    if (queuedGpu)
                    {
                        if (signalFrameAvailable)
                        {
                            NotifyFrameAvailable();
                        }

                        return true;
                    }

                    if (texture is not null)
                    {
                        ReleaseGpuTexture(texture);
                    }
                    return false;
                }

                DiagnosticLog.Write("VIDEO_PIPELINE", "GPU_SURFACE_PATH_FAILED falling back to CPU NV12 conversion");
                _gpuSurfacePipelineActive = false;
                if (!TryConvertNv12Sample(sample, out var fallbackPixels))
                {
                    return false;
                }

                var queuedFallback = false;
                var signalFallbackAvailable = false;
                lock (_videoQueueGate)
                {
                    if (_videoQueue.Count < MaxVideoQueueDepth)
                    {
                        signalFallbackAvailable = _videoQueue.Count == 0;
                        _videoQueue.Enqueue(new VideoFrame(fallbackPixels, null, 0, presentationTime, duration));
                        Interlocked.Increment(ref _decodedFrameCount);
                        Volatile.Write(ref _decodedVideoPositionSeconds, presentationTime / 10_000_000d);
                        queuedFallback = true;
                    }
                }

                if (queuedFallback && signalFallbackAvailable)
                {
                    NotifyFrameAvailable();
                }

                if (!queuedFallback)
                {
                    ReturnPixelBuffer(fallbackPixels);
                }

                return queuedFallback;
            }

            using (ThreadCpuDiagnostics.StartActivity("Olivia.VideoConvert"))
            {
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var data, out _, out var currentLength);
                var expectedLength = checked(Width * Height * 4);
                byte[]? pixels = null;
                var queuedBuffer = false;
                var signalBufferAvailable = false;
                try
                {
                    pixels = ArrayPool<byte>.Shared.Rent(expectedLength);
                    var copyLength = Math.Min(expectedLength, currentLength);
                    Marshal.Copy(data, pixels, 0, copyLength);
                    if (copyLength < expectedLength)
                    {
                        pixels.AsSpan(copyLength, expectedLength - copyLength).Clear();
                    }
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
                            signalBufferAvailable = _videoQueue.Count == 0;
                            _videoQueue.Enqueue(new VideoFrame(pixels, null, 0, presentationTime, duration));
                            Interlocked.Increment(ref _decodedFrameCount);
                            Volatile.Write(ref _decodedVideoPositionSeconds, presentationTime / 10_000_000d);
                            if (!_startupFirstSampleLogged)
                            {
                                _startupFirstSampleLogged = true;
                                DiagnosticLog.Write("startup", $"FirstVideoSamplePTS={presentationTime / 10_000_000d:0.###} first_frame_converted=true queue_depth={_videoQueue.Count} startup_ms={_startupStopwatch?.Elapsed.TotalMilliseconds:0.0}");
                            }

                            queuedBuffer = true;
                        }
                    }

                    if (queuedBuffer)
                    {
                        if (signalBufferAvailable)
                        {
                            NotifyFrameAvailable();
                        }

                        return true;
                    }
                }
                finally
                {
                    buffer.Unlock();
                    if (!queuedBuffer && pixels is not null)
                    {
                        ReturnPixelBuffer(pixels);
                    }
                }
            }
            }
        }
        finally
        {
            if (sampleTracked)
            {
                GpuPipelineDiagnostics.DecoderSampleReleased();
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _lastDecodeMilliseconds = elapsed;
            _maxDecodeMilliseconds = Math.Max(_maxDecodeMilliseconds, elapsed);
            _totalDecodeMilliseconds += elapsed;
            _lastDecodeUtc = DateTime.UtcNow;
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

    private void NotifyFrameAvailable()
    {
        try
        {
            FrameAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("video", $"FrameAvailable notification failed: {ex.Message}");
        }
    }

    private bool TryGetGpuSurface(IMFSample sample, long frameId, out Vortice.Direct3D11.ID3D11Texture2D? texture, out uint subresourceIndex)
    {
        texture = null;
        subresourceIndex = 0;
        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgiBuffer is null)
            {
                DiagnosticLog.Write("VIDEO_PIPELINE", "GPU_SURFACE_PATH_FAILED reason=sample-buffer-is-not-IMFDXGIBuffer");
                return false;
            }

            subresourceIndex = dxgiBuffer.SubresourceIndex;
            using (var resourceStage = GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "IMFDXGIBuffer.GetResource", frameId))
            {
                texture = (Vortice.Direct3D11.ID3D11Texture2D)dxgiBuffer.GetResource(typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID);
            }
            GpuPipelineDiagnostics.DecoderTextureAcquired();
            var description = texture.Description;
            if (!UsesGpuSurface)
            {
                DiagnosticLog.Write("VIDEO_PIPELINE", $"DecoderOutput=GPU Surface DXGIBuffer=true Texture2D=true SubresourceIndex={subresourceIndex} TextureFormat={description.Format} Size={description.Width}x{description.Height} ArraySize={description.ArraySize} MipLevels={description.MipLevels} Usage={description.Usage} BindFlags={description.BindFlags} SampleDesc={description.SampleDescription.Count}x{description.SampleDescription.Quality} ColorConversion=D3D11 VideoProcessor RenderInput=GPU Texture EffectiveGpuSurfacePath=true CPUVideoPath=false");
            }
            return true;
        }
        catch (Exception exception)
        {
            texture?.Dispose();
            texture = null;
            DiagnosticLog.Write("VIDEO_PIPELINE", $"GPU_SURFACE_PATH_FAILED reason={exception.GetType().Name}:{exception.Message}");
            return false;
        }
    }

    private bool TryConvertNv12Sample(IMFSample sample, out byte[] pixels)
    {
        pixels = Array.Empty<byte>();
        try
        {
            var expectedYuvLength = checked(Width * Height + (Width * Height / 2));
            var expectedBgraLength = checked(Width * Height * 4);
            using var conversionActivity = ThreadCpuDiagnostics.StartActivity("Olivia.VideoConvert");
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var data, out _, out var currentLength);
            var yuv = ArrayPool<byte>.Shared.Rent(expectedYuvLength);
            try
            {
                var copyLength = Math.Min(expectedYuvLength, currentLength);
                Marshal.Copy(data, yuv, 0, copyLength);
                if (copyLength < expectedYuvLength)
                {
                    Array.Clear(yuv, copyLength, expectedYuvLength - copyLength);
                }

                pixels = ArrayPool<byte>.Shared.Rent(expectedBgraLength);
                ConvertNv12ToBgra(yuv, pixels, Width, Height);
                DiagnosticLog.Write("VIDEO_PIPELINE", "CPU_NV12_FALLBACK_ACTIVE reason=GPU_SURFACE_PATH_FAILED");
                return true;
            }
            catch
            {
                if (pixels.Length != 0)
                {
                    ReturnPixelBuffer(pixels);
                    pixels = Array.Empty<byte>();
                }

                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(yuv);
                buffer.Unlock();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("VIDEO_PIPELINE", $"CPU_NV12_FALLBACK_FAILED type={exception.GetType().Name} message={exception.Message}");
            return false;
        }
    }

    private static void ConvertNv12ToBgra(byte[] source, byte[] destination, int width, int height)
    {
        var yPlaneLength = checked(width * height);
        var uvOffset = yPlaneLength;
        var useBt709 = width >= 1280 || height >= 720;
        var yScale = 1.16438356;
        var rv = useBt709 ? 1.79274107 : 1.59602678;
        var gu = useBt709 ? -0.21324861 : -0.39176229;
        var gv = useBt709 ? -0.53290933 : -0.81296764;
        var bu = useBt709 ? 2.11240179 : 2.01723214;

        for (var y = 0; y < height; y++)
        {
            var yRow = y * width;
            var uvRow = uvOffset + (y / 2) * width;
            var destinationRow = yRow * 4;
            for (var x = 0; x < width; x++)
            {
                var luma = Math.Max(0, source[yRow + x] - 16) * yScale;
                var chromaIndex = uvRow + (x & ~1);
                var u = source[chromaIndex] - 128;
                var v = source[chromaIndex + 1] - 128;
                var r = Math.Clamp(luma + rv * v, 0, 255);
                var g = Math.Clamp(luma + gu * u + gv * v, 0, 255);
                var b = Math.Clamp(luma + bu * u, 0, 255);
                var output = destinationRow + x * 4;
                destination[output] = (byte)b;
                destination[output + 1] = (byte)g;
                destination[output + 2] = (byte)r;
                destination[output + 3] = 255;
            }
        }
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
        ClearVideoQueue();
        lock (_videoQueueGate)
        {
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
        List<VideoFrame>? frames = null;
        lock (_videoQueueGate)
        {
            while (_videoQueue.Count > 0)
            {
                (frames ??= new List<VideoFrame>(_videoQueue.Count)).Add(_videoQueue.Dequeue());
            }
        }

        if (frames is null)
        {
            return;
        }

        // Return decoder-owned textures outside the queue lock.  COM release
        // can wait for the decoder surface pool and must never block producers
        // or the scheduler while they hold _videoQueueGate.
        foreach (var frame in frames)
        {
            ReleaseVideoFrame(frame);
        }
    }

    private static void ReleaseVideoFrame(VideoFrame frame)
    {
        if (frame.Pixels is not null)
        {
            ReturnPixelBuffer(frame.Pixels);
        }

        if (frame.Texture is not null)
        {
            ReleaseGpuTexture(frame.Texture);
        }
    }

    private static void ReleaseGpuTexture(Vortice.Direct3D11.ID3D11Texture2D texture)
    {
        var role = string.Equals(Thread.CurrentThread.Name, "Olivia.VideoDecode", StringComparison.Ordinal)
            ? "Olivia.VideoDecode"
            : "Olivia.Render";
        using var releaseStage = GpuPipelineDiagnostics.Begin(role, "ReleaseDecoderTexture", 0);
        try
        {
            texture.Dispose();
        }
        finally
        {
            GpuPipelineDiagnostics.DecoderTextureReleased();
        }
    }

    private static void ReturnPixelBuffer(byte[] pixels)
    {
        ArrayPool<byte>.Shared.Return(pixels);
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
            using (GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "Flush", 0))
            {
                _reader.Flush(SourceReaderIndex.AllStreams);
            }
        }
        catch
        {
            try
            {
                using var videoFlushStage = GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "Flush.Video", 0);
                _reader.Flush(SourceReaderIndex.FirstVideoStream);
            }
            catch { }

            try
            {
                using var audioFlushStage = GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "Flush.Audio", 0);
                _reader.Flush(SourceReaderIndex.FirstAudioStream);
            }
            catch { }
        }

        var timestamp = checked((long)(Math.Max(0, seconds) * 10_000_000d));
        using (GpuPipelineDiagnostics.Begin("Olivia.VideoDecode", "SetCurrentPosition", 0, timestamp))
        {
            _reader.SetCurrentPosition(timestamp);
        }
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
                var frameId = GpuPipelineDiagnostics.NextFrameId();
                IMFSample? sample;
                using (var readStage = GpuPipelineDiagnostics.Begin("Olivia.AudioDecode", "ReadSample.Audio", frameId))
                {
                    sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out streamIndex, out flags, out timestamp100Ns);
                    readStage.SetPresentationTime(timestamp100Ns);
                }
                using (sample)
                {
                    if (sample is not null)
                    {
                        GpuPipelineDiagnostics.DecoderSampleAcquired();
                        try
                        {
                            ProcessAudioSample(sample, flags, timestamp100Ns);
                        }
                        finally
                        {
                            GpuPipelineDiagnostics.DecoderSampleReleased();
                        }
                    }
                    else
                    {
                        ProcessAudioSample(null, flags, timestamp100Ns);
                    }
                }
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
                var bytes = ArrayPool<byte>.Shared.Rent(currentLength);
                try
                {
                    Marshal.Copy(data, bytes, 0, currentLength);
                    var offset = GetAudioTrimOffset(sample, timestamp100Ns, currentLength, _audioOutput.WaveFormat.BlockAlign);
                    if (offset < currentLength)
                    {
                        _audioOutput.AddSamples(bytes, offset, currentLength - offset);
                        _lastAudioSubmitUtc = DateTime.UtcNow;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
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
        var decodedDelta = decoded - _lastCounterDecoded;
        var decodeMillisecondsDelta = _totalDecodeMilliseconds - _lastCounterDecodeMilliseconds;
        var decodedPerSecond = (decoded - _lastCounterDecoded) / elapsed;
        var presentedPerSecond = (presented - _lastCounterPresented) / elapsed;
        var renderTicksPerSecond = (renderTicks - _lastCounterRenderTicks) / elapsed;
        var presentCallsPerSecond = (presentCalls - _lastCounterPresentCalls) / elapsed;
        var readSampleAverageMilliseconds = decodedDelta > 0
            ? decodeMillisecondsDelta / decodedDelta
            : _lastDecodeMilliseconds;
        _lastCounterDecoded = decoded;
        _lastCounterDecodeMilliseconds = _totalDecodeMilliseconds;
        _lastCounterPresented = presented;
        _lastCounterRenderTicks = renderTicks;
        _lastCounterPresentCalls = presentCalls;
        _lastCounterTimeUtc = now;
        return new VideoPerformanceCounters(
            _sourceFrameRate,
            Math.Max(0, decodedPerSecond),
            Math.Max(0, presentedPerSecond),
            Math.Max(0, renderTicksPerSecond),
            Math.Max(0, presentCallsPerSecond),
            Math.Max(0, readSampleAverageMilliseconds),
            Math.Max(0, _maxDecodeMilliseconds));
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
        return $"subtype={DescribeVideoSubtype(subtype)} yuvMatrix={TryGetEnum<VideoTransferMatrix>(type, MediaTypeAttributeKeys.YuvMatrix)} nominalRange={TryGetEnum<NominalRange>(type, MediaTypeAttributeKeys.VideoNominalRange)} primaries={TryGetEnum<VideoPrimaries>(type, MediaTypeAttributeKeys.VideoPrimaries)} transfer={TryGetEnum<VideoTransferFunction>(type, MediaTypeAttributeKeys.TransferFunction)}";
    }

    private static string DescribeVideoSubtype(Guid subtype) => subtype switch
    {
        var value when value == VideoFormatGuids.H264 => "H264",
        var value when value == VideoFormatGuids.Hevc => "HEVC",
        var value when value == VideoFormatGuids.Vp80 => "VP8",
        var value when value == VideoFormatGuids.Vp90 => "VP9",
        var value when value == VideoFormatGuids.Rgb32 => "RGB32",
        var value when value == VideoFormatGuids.Argb32 => "ARGB32",
        var value when value == Nv12Subtype => "NV12",
        var value when value == P010Subtype => "P010",
        var value when value == Yuy2Subtype => "YUY2",
        var value when value == UyvySubtype => "UYVY",
        var value when value == Yv12Subtype => "YV12",
        var value when value == IyuvSubtype => "IYUV",
        var value when value == YvyuSubtype => "YVYU",
        var value when value == I420Subtype => "I420",
        var value when value == Guid.Empty => "unset",
        _ => subtype.ToString("D")
    };

    private readonly record struct DecoderCandidateInfo(
        string Name,
        Guid Clsid,
        uint Flags,
        bool IsHardware,
        bool IsAsync);

    private static DecoderCandidateInfo[] LogDecoderCandidates(Guid inputSubtype)
    {
        if (inputSubtype == Guid.Empty)
        {
            DiagnosticLog.Write("MFT_CANDIDATE", "Codec=unknown; candidate enumeration skipped because InputSubtype is unset");
            return Array.Empty<DecoderCandidateInfo>();
        }

        var inputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = inputSubtype
        };

        var hardware = LogDecoderCandidateSet("Hardware", (uint)EnumFlag.EnumFlagHardware, inputType);
        var softwareFlags = (uint)EnumFlag.EnumFlagSyncmft
            | (uint)EnumFlag.EnumFlagAsyncmft
            | (uint)EnumFlag.EnumFlagLocalmft;
        var software = LogDecoderCandidateSet("Software", softwareFlags, inputType);
        return hardware.Concat(software).ToArray();
    }

    private static List<DecoderCandidateInfo> LogDecoderCandidateSet(string setName, uint flags, RegisterTypeInfo inputType)
    {
        var candidates = new List<DecoderCandidateInfo>();
        try
        {
            using var activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder,
                flags,
                inputType,
                null);

            var count = 0;
            foreach (var activation in activations)
            {
                using (activation)
                {
                    count++;
                    var clsid = TryGetGuid(activation, TransformAttributeKeys.MftTransformClsidAttribute);
                    var transformFlags = TryGetUInt32(activation, TransformAttributeKeys.TransformFlagsAttribute, 0);
                    var isHardware = (transformFlags & (uint)EnumFlag.EnumFlagHardware) != 0 || setName == "Hardware";
                    var isAsync = (transformFlags & (uint)EnumFlag.EnumFlagAsyncmft) != 0
                        || (transformFlags & (uint)EnumFlag.EnumFlagHardware) != 0;
                    var friendlyName = TryGetString(activation, TransformAttributeKeys.MftFriendlyNameAttribute);
                    if (string.IsNullOrWhiteSpace(friendlyName))
                    {
                        friendlyName = TryGetFriendlyName(activation);
                    }
                    var inputTypes = DescribeRegisteredTypes(activation, TransformAttributeKeys.MftInputTypesAttributes);
                    var outputTypes = DescribeRegisteredTypes(activation, TransformAttributeKeys.MftOutputTypesAttributes);
                    candidates.Add(new DecoderCandidateInfo(friendlyName, clsid, transformFlags, isHardware, isAsync));
                    DiagnosticLog.Write("MFT_CANDIDATE", $"Set={setName} Name={friendlyName} CLSID={(clsid == Guid.Empty ? "unknown" : clsid.ToString("D"))} Flags=0x{transformFlags:X8} IsHardwareMFT={isHardware} IsAsyncMFT={isAsync} SupportedInputTypes={inputTypes} SupportedOutputTypes={outputTypes}");
                }
            }

            DiagnosticLog.Write("MFT_CANDIDATE", $"Set={setName} Count={count} QueryFlags=0x{flags:X8} Codec={DescribeVideoSubtype(inputType.GuidSubtype)}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("MFT_CANDIDATE", $"Set={setName} enumeration_failed type={exception.GetType().Name} message={exception.Message}");
        }

        return candidates;
    }

    private static string TryGetFriendlyName(IMFActivate activation)
    {
        try
        {
            return string.IsNullOrWhiteSpace(activation.FriendlyName) ? "unnamed" : activation.FriendlyName;
        }
        catch
        {
            return "unnamed";
        }
    }

    private static string DescribeRegisteredTypes(IMFActivate activation, Guid attributeKey)
    {
        try
        {
            var blob = activation.GetBlob(attributeKey);
            const int registerTypeInfoSize = 32;
            if (blob is null || blob.Length < registerTypeInfoSize)
            {
                return "none";
            }

            var types = new List<string>(blob.Length / registerTypeInfoSize);
            for (var offset = 0; offset + registerTypeInfoSize <= blob.Length; offset += registerTypeInfoSize)
            {
                var majorType = new Guid(blob.AsSpan(offset, 16));
                var subtype = new Guid(blob.AsSpan(offset + 16, 16));
                types.Add($"{DescribeMajorType(majorType)}/{DescribeVideoSubtype(subtype)}");
            }

            return types.Count == 0 ? "none" : string.Join(",", types);
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string DescribeMajorType(Guid majorType) =>
        majorType == MediaTypeGuids.Video ? "Video"
        : majorType == MediaTypeGuids.Audio ? "Audio"
        : majorType == Guid.Empty ? "unset"
        : majorType.ToString("D");

    private static string DescribeTransformCategory(Guid category) =>
        category == TransformCategoryGuids.VideoDecoder ? "VideoDecoder"
        : category == TransformCategoryGuids.VideoProcessor ? "VideoProcessor"
        : category == TransformCategoryGuids.AudioDecoder ? "AudioDecoder"
        : category == Guid.Empty ? "unknown"
        : category.ToString("D");

    private static void LogVideoDecoderChain(
        IMFSourceReader reader,
        Guid inputSubtype,
        IMFMediaType outputType,
        IReadOnlyList<DecoderCandidateInfo> candidates,
        bool dxgiManagerAttached)
    {
        var decoderName = "SourceReader automatic MFT (not exposed)";
        var decoderClsid = Guid.Empty;
        var decoderCategory = "unknown";
        var decoderHardware = "unknown";
        var decoderAsync = "unknown";
        var decoderD3D11Aware = "unknown";
        var transformCount = 0;
        try
        {
            using var readerEx = reader.QueryInterfaceOrNull<IMFSourceReaderEx>();
            if (readerEx is not null)
            {
                for (var index = 0; index < 16; index++)
                {
                    IMFTransform? transform = null;
                    var result = readerEx.GetTransformForStream((int)SourceReaderIndex.FirstVideoStream, index, out var category, out transform);
                    if (result.Failure || transform is null)
                    {
                        break;
                    }

                    transformCount++;
                    try
                    {
                        using var transformAttributes = transform.Attributes;
                        var name = TryGetString(transformAttributes, TransformAttributeKeys.MftFriendlyNameAttribute);
                        var clsid = TryGetGuid(transformAttributes, TransformAttributeKeys.MftTransformClsidAttribute);
                        var transformFlags = TryGetUInt32(transformAttributes, TransformAttributeKeys.TransformFlagsAttribute, 0);
                        var asyncValue = TryGetUInt32(transformAttributes, TransformAttributeKeys.TransformAsync, 0);
                        var d3dAware = TryGetUInt32(transformAttributes, TransformAttributeKeys.D3D11Aware, 0);
                        var connectedToHardware = TryGetUInt32(transformAttributes, TransformAttributeKeys.MftConnectedToHwStream, 0);
                        var hardwareUrl = TryGetString(transformAttributes, TransformAttributeKeys.MftEnumHardwareUrlAttribute);
                        var isHardware = (transformFlags & (uint)EnumFlag.EnumFlagHardware) != 0
                            || connectedToHardware != 0
                            || !string.IsNullOrWhiteSpace(hardwareUrl);
                        var isAsync = asyncValue != 0
                            || (transformFlags & (uint)EnumFlag.EnumFlagAsyncmft) != 0
                            || isHardware;
                        var looksLikeDecoder = name.Contains("decoder", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("decode", StringComparison.OrdinalIgnoreCase);
                        if (decoderClsid == Guid.Empty && (looksLikeDecoder || transformCount == 1))
                        {
                            decoderName = string.IsNullOrWhiteSpace(name) ? $"MFT[{index}]" : name;
                            decoderClsid = clsid;
                            decoderCategory = DescribeTransformCategory(category);
                            decoderHardware = isHardware ? "true" : "false";
                            decoderAsync = isAsync ? "true" : "false";
                            decoderD3D11Aware = d3dAware == 0 ? "false_or_unreported" : "true";
                        }

                        DiagnosticLog.Write("VIDEO_TRANSFORM", $"index={index} category={DescribeTransformCategory(category)} categoryGuid={category:D} Name={name switch { "" => "unset", _ => name }} CLSID={(clsid == Guid.Empty ? "unset" : clsid.ToString("D"))} Flags=0x{transformFlags:X8} IsHardwareMFT={isHardware} IsAsyncMFT={isAsync} D3D11Aware={(d3dAware == 0 ? "false_or_unreported" : "true")} ConnectedToHardware={(connectedToHardware == 0 ? "false_or_unreported" : "true")} HardwareUrl={(string.IsNullOrWhiteSpace(hardwareUrl) ? "unset" : hardwareUrl)}");
                    }
                    finally
                    {
                        transform.Dispose();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("VIDEO_DECODER", $"SourceReaderTransformProbe=failed type={exception.GetType().Name} message={exception.Message}");
        }

        // Some Windows Source Reader transforms expose no friendly-name or
        // CLSID attributes after activation.  If enumeration found exactly
        // one software decoder for this codec, use that sole candidate as a
        // conservative identity match and keep the runtime transform probe
        // evidence in the log above.
        if (transformCount == 1 && decoderClsid == Guid.Empty)
        {
            var softwareCandidates = candidates.Where(candidate => !candidate.IsHardware).ToArray();
            if (softwareCandidates.Length == 1)
            {
                var candidate = softwareCandidates[0];
                decoderName = candidate.Name;
                decoderClsid = candidate.Clsid;
                decoderHardware = candidate.IsHardware ? "true" : "false";
                decoderAsync = candidate.IsAsync ? "true" : "false";
                DiagnosticLog.Write("DECODER_INFO", $"SelectedDecoderMatchedCandidate=true Match=sole-software-candidate CLSID={(decoderClsid == Guid.Empty ? "unknown" : decoderClsid.ToString("D"))}");
            }
        }

        var outputSubtype = TryGetGuid(outputType, MediaTypeAttributeKeys.Subtype);
        var cpuOutput = outputSubtype == VideoFormatGuids.Rgb32 || outputSubtype == VideoFormatGuids.Argb32;
        var decoderType = decoderHardware == "true"
            ? "Hardware MFT"
            : decoderHardware == "false"
                ? (cpuOutput ? "Software MFT" : "Software MFT with GPU Surface")
                : "Unknown MFT";
        var decoderOutput = cpuOutput ? "CPU Buffer" : "GPU Surface or unknown";
        var colorConversion = cpuOutput ? "CPU" : "GPU or unknown";
        var renderInput = cpuOutput ? "CPU Upload" : "GPU Texture or unknown";
        DiagnosticLog.Write("DECODER_INFO", $"Codec={DescribeVideoSubtype(inputSubtype)} DecoderFriendlyName={decoderName} DecoderCLSID={(decoderClsid == Guid.Empty ? "unknown" : decoderClsid.ToString("D"))} DecoderCategory={decoderCategory} IsHardwareMFT={decoderHardware} IsAsyncMFT={decoderAsync} D3D11Aware={decoderD3D11Aware}");
        DiagnosticLog.Write("VIDEO_DECODER", $"Codec={DescribeVideoSubtype(inputSubtype)} InputSubtype={DescribeVideoSubtype(inputSubtype)} OutputSubtype={DescribeVideoSubtype(outputSubtype)} DecoderName={decoderName} DecoderCLSID={(decoderClsid == Guid.Empty ? "unknown" : decoderClsid.ToString("D"))} HardwareAccelerated={decoderHardware} HardwareSurfaceRequested={(dxgiManagerAttached && !cpuOutput)} D3D11Aware={decoderD3D11Aware} DXGIDeviceManagerAttached={dxgiManagerAttached} DecodeAdapter={(dxgiManagerAttached ? "render-device" : "unknown(reason=no_source_reader_dxgi_manager)")} EffectiveGpuSurfacePath={(cpuOutput ? "false" : dxgiManagerAttached ? "pending" : "unknown")} TransformCount={transformCount}");
        DiagnosticLog.Write("VIDEO_PIPELINE", $"Decoder={decoderName} DecoderType={decoderType} InputSubtype={DescribeVideoSubtype(inputSubtype)} OutputSubtype={DescribeVideoSubtype(outputSubtype)} DecoderOutput={decoderOutput} ColorConversion={colorConversion} RenderInput={renderInput}");
        if (cpuOutput)
        {
            DiagnosticLog.Write("VIDEO_DECODER", "CPU_VIDEO_PATH_DETECTED output is RGB32/ARGB32; SourceReader color conversion and CPU readback path are active");
            DiagnosticLog.Write("VIDEO_DECODER", "CPU_PIXEL_PATH ConvertToContiguousBuffer -> Marshal.Copy -> D3D11 dynamic texture upload; no GPU NV12 surface is consumed by the current renderer");
        }
    }

    private static void ProbeVideoSampleBuffer(IMFSample sample)
    {
        try
        {
            var bufferCount = sample.BufferCount;
            if (bufferCount <= 0)
            {
                DiagnosticLog.Write("DECODER_INFO", "SampleBufferType=none DXGIBuffer=false CPUBuffer=false BufferCount=0");
                DiagnosticLog.Write("VIDEO_DECODER", "WARNING_CPU_VIDEO_BUFFER SampleBufferCount=0");
                return;
            }

            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgiBuffer is null)
            {
                DiagnosticLog.Write("DECODER_INFO", $"SampleBufferType=CPUBuffer DXGIBuffer=false CPUBuffer=true BufferCount={bufferCount}");
                DiagnosticLog.Write("VIDEO_DECODER", $"WARNING_CPU_VIDEO_BUFFER SampleBufferCount={bufferCount} IMFDXGIBuffer=false BufferType=CPU_MEMORY");
                return;
            }

            var textureDescription = "unknown";
            try
            {
                using var texture = (ID3D11Texture2D)dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                var description = texture.Description;
                textureDescription = $"{description.Width}x{description.Height} {description.Format}";
            }
            catch (Exception exception)
            {
                textureDescription = $"texture_query_failed:{exception.GetType().Name}";
            }

            DiagnosticLog.Write("DECODER_INFO", $"SampleBufferType=GPUBuffer DXGIBuffer=true Texture2D={(textureDescription.StartsWith("texture_query_failed", StringComparison.OrdinalIgnoreCase) ? "false" : "true")} CPUBuffer=false BufferCount={bufferCount} SubresourceIndex={dxgiBuffer.SubresourceIndex} TextureFormat={textureDescription}");
            DiagnosticLog.Write("VIDEO_DECODER", $"SampleBufferCount={bufferCount} IMFDXGIBuffer=true GPUTexture={textureDescription} Subresource={dxgiBuffer.SubresourceIndex}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("DECODER_INFO", $"SampleBufferType=unknown probe_failed type={exception.GetType().Name} message={exception.Message}");
            DiagnosticLog.Write("VIDEO_DECODER", $"SampleBufferProbe=failed type={exception.GetType().Name} message={exception.Message}");
        }
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

    private static Guid TryGetGuid(IMFAttributes type, Guid key)
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

    private static string TryGetEnum<T>(IMFAttributes type, Guid key) where T : struct, Enum
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

    private static uint TryGetUInt32(IMFAttributes attributes, Guid key, uint fallback)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch
        {
            return fallback;
        }
    }

    private static string TryGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
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
