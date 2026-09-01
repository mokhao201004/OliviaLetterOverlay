using System.Collections.Concurrent;
using System.Diagnostics;

namespace OliviaLetterOverlay;

/// <summary>
/// 非侵入式 GPU 管线诊断，默认只把首批阶段和慢调用写入日志，避免诊断本身阻塞播放。
/// </summary>
internal static class GpuPipelineDiagnostics
{
    private const double WarningMilliseconds = 50;
    private const double StallMilliseconds = 200;
    private const double HangMilliseconds = 1000;
    private const double WatchdogMilliseconds = 2000;
    private static readonly ConcurrentDictionary<string, Heartbeat> Heartbeats = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> StageLogCounts = new(StringComparer.Ordinal);
    private static readonly object ProviderGate = new();
    private static Func<string>? _snapshotProvider;
    private static int _watchdogStarted;
    private static long _frameId;
    private static int _outstandingDecoderSamples;
    private static int _maxOutstandingDecoderSamples;
    private static int _outstandingGpuFrames;
    private static int _maxOutstandingGpuFrames;
    private static int _inputViewsCreated;
    private static int _inputViewsReleased;

    public static int OutstandingDecoderSamples => Math.Max(0, Volatile.Read(ref _outstandingDecoderSamples));
    public static int MaxOutstandingDecoderSamples => Math.Max(0, Volatile.Read(ref _maxOutstandingDecoderSamples));
    public static int OutstandingGpuFrames => Math.Max(0, Volatile.Read(ref _outstandingGpuFrames));
    public static int MaxOutstandingGpuFrames => Math.Max(0, Volatile.Read(ref _maxOutstandingGpuFrames));
    public static int InputViewsCreated => Math.Max(0, Volatile.Read(ref _inputViewsCreated));
    public static int InputViewsReleased => Math.Max(0, Volatile.Read(ref _inputViewsReleased));

    public static long NextFrameId() => Interlocked.Increment(ref _frameId);

    public static void RegisterThread(string role, string stage = "idle", long frameId = 0)
    {
        var heartbeat = Heartbeats.GetOrAdd(role, static key => new Heartbeat(key));
        heartbeat.Mark(stage, frameId);
    }

    public static void MarkProgress(string role, string? stage = null, long frameId = 0)
    {
        var heartbeat = Heartbeats.GetOrAdd(role, static key => new Heartbeat(key));
        heartbeat.Mark(stage, frameId, stageStarted: true);
    }

    public static StageScope Begin(string role, string stage, long frameId, long pts100Ns = 0)
    {
        var heartbeat = Heartbeats.GetOrAdd(role, static key => new Heartbeat(key));
        heartbeat.Mark(stage, frameId);
        var logBegin = StageLogCounts.AddOrUpdate(stage, 1, static (_, count) => count + 1) <= 3;
        if (logBegin)
        {
            DiagnosticLog.Write("GPU_STAGE", $"[GPU_STAGE_BEGIN] Stage={stage} FrameId={frameId} PTS={pts100Ns / 10_000_000d:0.######} ThreadId={Environment.CurrentManagedThreadId}");
        }

        return new StageScope(heartbeat, role, stage, frameId, pts100Ns, logBegin);
    }

    public static void SetSnapshotProvider(Func<string> provider)
    {
        lock (ProviderGate)
        {
            _snapshotProvider = provider;
        }
    }

    public static void StartWatchdog()
    {
        if (Interlocked.Exchange(ref _watchdogStarted, 1) != 0)
        {
            return;
        }

        var thread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "Olivia.GpuWatchdog",
        };
        thread.Start();
    }

    public static void DecoderSampleAcquired()
    {
        var current = Interlocked.Increment(ref _outstandingDecoderSamples);
        UpdateMaximum(ref _maxOutstandingDecoderSamples, current);
    }

    public static void DecoderSampleReleased() => Decrement(ref _outstandingDecoderSamples);

    public static void DecoderTextureAcquired()
    {
        var current = Interlocked.Increment(ref _outstandingGpuFrames);
        UpdateMaximum(ref _maxOutstandingGpuFrames, current);
    }

    public static void DecoderTextureReleased() => Decrement(ref _outstandingGpuFrames);

    public static void InputViewCreated() => Interlocked.Increment(ref _inputViewsCreated);
    public static void InputViewReleased() => Interlocked.Increment(ref _inputViewsReleased);

    public struct StageScope : IDisposable
    {
        private readonly Heartbeat _heartbeat;
        private readonly string _role;
        private readonly string _stage;
        private readonly long _frameId;
        private long _pts100Ns;
        private readonly long _startedAt;
        private readonly bool _logBegin;
        private readonly int _threadId;
        private bool _disposed;

        internal StageScope(Heartbeat heartbeat, string role, string stage, long frameId, long pts100Ns, bool logBegin)
        {
            _heartbeat = heartbeat;
            _role = role;
            _stage = stage;
            _frameId = frameId;
            _pts100Ns = pts100Ns;
            _startedAt = Stopwatch.GetTimestamp();
            _logBegin = logBegin;
            _threadId = Environment.CurrentManagedThreadId;
            _disposed = false;
        }

        public void SetPresentationTime(long pts100Ns) => _pts100Ns = pts100Ns;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            _heartbeat.Mark("idle", _frameId);
            var severity = elapsed >= HangMilliseconds
                ? " HANG"
                : elapsed >= StallMilliseconds
                    ? " STALL"
                    : elapsed >= WarningMilliseconds
                        ? " WARNING"
                        : string.Empty;
            if (_logBegin || elapsed >= WarningMilliseconds)
            {
                DiagnosticLog.Write("GPU_STAGE", $"[GPU_STAGE_END] Stage={_stage} FrameId={_frameId} PTS={_pts100Ns / 10_000_000d:0.######} ElapsedMs={elapsed:0.0} ThreadId={_threadId}{severity}");
            }
        }
    }

    private static void WatchdogLoop()
    {
        while (true)
        {
            Thread.Sleep(500);
            var now = Stopwatch.GetTimestamp();
            foreach (var heartbeat in Heartbeats.Values)
            {
                var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref heartbeat.LastProgressTimestamp)).TotalMilliseconds;
                if (elapsed < WatchdogMilliseconds
                    || heartbeat.IsInactive
                    || Interlocked.Exchange(ref heartbeat.HangLogged, 1) != 0)
                {
                    continue;
                }

                var snapshot = string.Empty;
                try
                {
                    lock (ProviderGate)
                    {
                        snapshot = _snapshotProvider?.Invoke() ?? string.Empty;
                    }
                }
                catch (Exception exception)
                {
                    snapshot = $"SnapshotError={exception.GetType().Name}:{exception.Message}";
                }

                var all = string.Join("; ", Heartbeats.Values.Select(item => item.Describe(now)));
                DiagnosticLog.Write("PIPELINE_HANG", $"[PIPELINE_HANG] Role={heartbeat.Role} ElapsedMs={elapsed:0} {all} Snapshot={snapshot} OutstandingDecoderSamples={OutstandingDecoderSamples} MaxOutstandingDecoderSamples={MaxOutstandingDecoderSamples} OutstandingGpuFrames={OutstandingGpuFrames} MaxOutstandingGpuFrames={MaxOutstandingGpuFrames} InputViewsCreated={InputViewsCreated} InputViewsReleased={InputViewsReleased}");
            }
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static void Decrement(ref int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current <= 0 || Interlocked.CompareExchange(ref value, current - 1, current) == current)
            {
                return;
            }
        }
    }

    internal sealed class Heartbeat
    {
        public Heartbeat(string role)
        {
            Role = role;
            LastProgressTimestamp = Stopwatch.GetTimestamp();
        }

        public string Role { get; }
        public long LastProgressTimestamp;
        public int HangLogged;
        public string CurrentStage = "idle";
        public long CurrentFrameId;
        public long CurrentStageStartedTimestamp = Stopwatch.GetTimestamp();
        public bool IsInactive
        {
            get
            {
                var stage = Volatile.Read(ref CurrentStage);
                return stage.Equals("idle", StringComparison.OrdinalIgnoreCase)
                    || stage.Equals("startup", StringComparison.OrdinalIgnoreCase)
                    || stage.Contains("Wait", StringComparison.OrdinalIgnoreCase);
            }
        }

        public void Mark(string? stage, long frameId, bool stageStarted = false)
        {
            if (!string.IsNullOrWhiteSpace(stage))
            {
                Volatile.Write(ref CurrentStage, stage);
                if (stageStarted)
                {
                    Volatile.Write(ref CurrentStageStartedTimestamp, Stopwatch.GetTimestamp());
                }
            }
            if (frameId != 0)
            {
                Volatile.Write(ref CurrentFrameId, frameId);
            }
            Volatile.Write(ref LastProgressTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref HangLogged, 0);
        }

        public string Describe(long now)
        {
            var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref LastProgressTimestamp)).TotalMilliseconds;
            var stageElapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref CurrentStageStartedTimestamp)).TotalMilliseconds;
            return $"{Role}.LastProgressMs={elapsed:0} {Role}.CurrentStage={Volatile.Read(ref CurrentStage)} {Role}.CurrentStageElapsedMs={stageElapsed:0} {Role}.CurrentFrameId={Volatile.Read(ref CurrentFrameId)}";
        }
    }
}
