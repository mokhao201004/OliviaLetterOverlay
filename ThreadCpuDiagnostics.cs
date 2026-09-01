using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OliviaLetterOverlay;

/// <summary>
/// Lightweight, once-per-second CPU attribution for Olivia's playback stages.
/// ProcessThread.TotalProcessorTime is the authoritative OS-thread measure.
/// Stage activity and wakeups are instrumentation counters, so UI-hosted stages
/// can still be attributed without pretending they are separate OS threads.
/// </summary>
internal static class ThreadCpuDiagnostics
{
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    private static readonly ConcurrentDictionary<string, RoleState> Roles = new(StringComparer.Ordinal);
    private static readonly object SampleGate = new();
    private static readonly Dictionary<int, double> PreviousCpuSeconds = new();
    private static readonly Dictionary<int, string> DescriptionCache = new();
    private static DateTime _lastSampleUtc;
    private static bool _configurationLogged;

    static ThreadCpuDiagnostics()
    {
        AddRole("Olivia.VideoDecode", "MediaFoundationVideoDecoder.DecodeLoop -> ReadAndQueueVideoFrame");
        AddRole("Olivia.VideoConvert", "MediaFoundationVideoDecoder.ReadAndQueueVideoFrame -> ConvertToContiguousBuffer/Marshal.Copy");
        AddRole("Olivia.VideoScheduler", "DesktopWallpaperWindow.RenderNextFrame -> TryReadFrame");
        AddRole("Olivia.Render", "D3D11Renderer.PresentFrame/RenderBlack -> Present");
        AddRole("Olivia.AudioDecode", "MediaFoundationVideoDecoder.DecodeLoop -> PumpAudioSamples");
        AddRole("Olivia.AudioOutput", "WasapiAudioOutput.AddSamples -> WASAPI submit");
        AddRole("Olivia.PlaybackCoordinator", "WallpaperTransitionController.Tick");
        AddRole("Olivia.DesktopMonitor", "DesktopWallpaperWindow.RequestMonitorRefresh/RefreshMonitorBounds");
        AddRole("Olivia.UI", "WPF Dispatcher/MainWindow");
    }

    public static void RegisterCurrentThread(string role)
    {
        var state = GetRole(role);
        var threadId = unchecked((int)GetCurrentThreadId());
        Volatile.Write(ref state.ThreadId, threadId);

        try
        {
            if (Thread.CurrentThread.Name is null)
            {
                Thread.CurrentThread.Name = role;
            }
        }
        catch (InvalidOperationException)
        {
            // Thread.Name may already have been assigned by the runtime.
        }

        TrySetThreadDescription(role);
    }

    public static void MarkWakeup(string role)
    {
        var state = GetRole(role);
        EnsureLogicalThread(state);
        Interlocked.Increment(ref state.Wakeups);
    }

    public static ActivityScope StartActivity(string role)
    {
        var state = GetRole(role);
        EnsureLogicalThread(state);
        return new ActivityScope(state, Stopwatch.GetTimestamp());
    }

    public static void LogConfigurationOnce()
    {
        if (_configurationLogged)
        {
            return;
        }

        lock (SampleGate)
        {
            if (_configurationLogged)
            {
                return;
            }

            _configurationLogged = true;
            DiagnosticLog.Write("THREAD_CPU", "TimerInventory frame=16ms progress=250ms transition=16ms composition_target_rendering=false; wakeups=instrumented stage activity, ThreadCpuPercent=ProcessThread.TotalProcessorTime");
        }
    }

    public static void Sample()
    {
        var now = DateTime.UtcNow;
        lock (SampleGate)
        {
            var wallSeconds = _lastSampleUtc == default
                ? 1d
                : Math.Max(0.001, (now - _lastSampleUtc).TotalSeconds);
            _lastSampleUtc = now;

            var current = new Dictionary<int, ThreadSample>();
            try
            {
                using var process = Process.GetCurrentProcess();
                foreach (ProcessThread processThread in process.Threads)
                {
                    try
                    {
                        var threadId = processThread.Id;
                        var cpuSeconds = processThread.TotalProcessorTime.TotalSeconds;
                        var deltaSeconds = PreviousCpuSeconds.TryGetValue(threadId, out var previous)
                            ? Math.Max(0, cpuSeconds - previous)
                            : 0;
                        var label = FindRoleLabel(threadId) ?? GetThreadDescription(threadId) ?? $"Thread#{threadId}";
                        current[threadId] = new ThreadSample(
                            threadId,
                            label,
                            deltaSeconds * 1000,
                            Math.Clamp(deltaSeconds / (wallSeconds * Math.Max(1, Environment.ProcessorCount)) * 100, 0, 100));
                        PreviousCpuSeconds[threadId] = cpuSeconds;
                    }
                    catch (InvalidOperationException)
                    {
                        // A thread can exit between enumeration and sampling.
                    }
                    catch (ArgumentException)
                    {
                        // The process thread may disappear during sampling.
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DiagnosticLog.Write("THREAD_CPU", $"sample_failed type={exception.GetType().Name} message={exception.Message}");
            }

            var roleRows = Roles.Values
                .OrderBy(state => state.Role, StringComparer.Ordinal)
                .Select(state => BuildRoleRow(state, current, wallSeconds))
                .ToArray();

            DiagnosticLog.Write("THREAD_CPU", "[THREAD_CPU] mode=OS-thread CPU plus logical-stage activity; logical rows sharing one TID are not additive");
            foreach (var row in roleRows)
            {
                DiagnosticLog.Write("THREAD_CPU", $"[THREAD_CPU] {row.Role} tid={row.ThreadId} shared_tid={row.SharedThread.ToString().ToLowerInvariant()} ThreadCpuTimeDeltaMs={row.CpuDeltaMs:0.0} ThreadCpuPercent={row.CpuPercent:0.0}% EstimatedCpuPercent={row.EstimatedPercent:0.0}% WakeupsPerSecond={row.WakeupsPerSecond:0.0} Path={row.Path}");
            }

            foreach (var thread in current.Values
                .OrderByDescending(sample => sample.CpuPercent)
                .ThenBy(sample => sample.ThreadId)
                .Take(3))
            {
                var path = FindRolePath(thread.ThreadId) ?? "unclassified process thread";
                DiagnosticLog.Write("THREAD_CPU", $"[THREAD_CPU_TOP] tid={thread.ThreadId} name={thread.Label} ThreadCpuTimeDeltaMs={thread.CpuDeltaMs:0.0} ThreadCpuPercent={thread.CpuPercent:0.0}% Path={path}");
            }

            foreach (var state in Roles.Values)
            {
                Interlocked.Exchange(ref state.Wakeups, 0);
                Interlocked.Exchange(ref state.ActiveTicks, 0);
            }
        }
    }

    public struct ActivityScope : IDisposable
    {
        private RoleState? _state;
        private readonly long _startedAt;

        internal ActivityScope(RoleState state, long startedAt)
        {
            _state = state;
            _startedAt = startedAt;
        }

        public void Dispose()
        {
            var state = _state;
            if (state is null)
            {
                return;
            }

            _state = null;
            var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - _startedAt);
            Interlocked.Add(ref state.ActiveTicks, elapsed);
        }
    }

    private static RoleState GetRole(string role) => Roles.GetOrAdd(role, static key => new RoleState(key, "unclassified stage"));

    private static void AddRole(string role, string path) => Roles[role] = new RoleState(role, path);

    private static void EnsureLogicalThread(RoleState state)
    {
        if (Volatile.Read(ref state.ThreadId) == 0)
        {
            Volatile.Write(ref state.ThreadId, unchecked((int)GetCurrentThreadId()));
        }
    }

    private static RoleRow BuildRoleRow(RoleState state, Dictionary<int, ThreadSample> current, double wallSeconds)
    {
        var threadId = Volatile.Read(ref state.ThreadId);
        var thread = default(ThreadSample);
        var hasThread = threadId != 0 && current.TryGetValue(threadId, out thread);
        var activeTicks = Interlocked.Exchange(ref state.ActiveTicks, 0);
        var wakeups = Interlocked.Exchange(ref state.Wakeups, 0);
        var estimatedPercent = Math.Clamp(activeTicks / (TicksPerSecond * wallSeconds) * 100, 0, 100);
        var shared = Roles.Values.Count(other => other != state && Volatile.Read(ref other.ThreadId) == threadId && threadId != 0) > 0;
        return new RoleRow(
            state.Role,
            threadId == 0 ? "-" : threadId.ToString(),
            hasThread ? thread.CpuDeltaMs : 0,
            hasThread ? thread.CpuPercent : 0,
            estimatedPercent,
            wakeups / wallSeconds,
            state.Path,
            shared);
    }

    private static string? FindRoleLabel(int threadId)
    {
        var role = Roles.Values
            .Where(state => Volatile.Read(ref state.ThreadId) == threadId)
            .OrderBy(state => state.Role, StringComparer.Ordinal)
            .FirstOrDefault();
        return role?.Role;
    }

    private static string? FindRolePath(int threadId)
    {
        var role = Roles.Values
            .Where(state => Volatile.Read(ref state.ThreadId) == threadId)
            .OrderBy(state => state.Role, StringComparer.Ordinal)
            .FirstOrDefault();
        return role?.Path;
    }

    private static string? GetThreadDescription(int threadId)
    {
        if (DescriptionCache.TryGetValue(threadId, out var cached))
        {
            return string.IsNullOrWhiteSpace(cached) ? null : cached;
        }

        var handle = OpenThread(ThreadQueryLimitedInformation, false, unchecked((uint)threadId));
        if (handle == IntPtr.Zero)
        {
            DescriptionCache[threadId] = string.Empty;
            return null;
        }

        try
        {
            var result = GetThreadDescription(handle, out var descriptionPointer);
            if (result == 0 && descriptionPointer != IntPtr.Zero)
            {
                var description = Marshal.PtrToStringUni(descriptionPointer);
                LocalFree(descriptionPointer);
                DescriptionCache[threadId] = description ?? string.Empty;
                return description;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 1607+ supports GetThreadDescription; keep fallback for older hosts.
        }
        finally
        {
            CloseHandle(handle);
        }

        DescriptionCache[threadId] = string.Empty;
        return null;
    }

    private static void TrySetThreadDescription(string role)
    {
        try
        {
            _ = SetThreadDescription(GetCurrentThread(), role);
        }
        catch (EntryPointNotFoundException)
        {
            // Managed Thread.Name and role registration remain available.
        }
        catch (DllNotFoundException)
        {
            // The process is Windows-only, but keep diagnostics non-fatal.
        }
    }

    internal sealed class RoleState
    {
        public RoleState(string role, string path)
        {
            Role = role;
            Path = path;
        }

        public string Role { get; }
        public string Path { get; }
        public int ThreadId;
        public long Wakeups;
        public long ActiveTicks;
    }

    private readonly record struct ThreadSample(int ThreadId, string Label, double CpuDeltaMs, double CpuPercent);

    private readonly record struct RoleRow(
        string Role,
        string ThreadId,
        double CpuDeltaMs,
        double CpuPercent,
        double EstimatedPercent,
        double WakeupsPerSecond,
        string Path,
        bool SharedThread);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetThreadDescription(IntPtr hThread, string lpThreadDescription);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
