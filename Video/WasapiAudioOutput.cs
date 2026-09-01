using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OliviaLetterOverlay.Video;

/// <summary>
/// Small WASAPI shared-mode sink for PCM samples decoded by Media Foundation.
/// WASAPI's consumed-byte position is exposed as the playback master clock;
/// this class also buffers PCM and exposes pause/resume/flush operations.
/// </summary>
internal sealed class WasapiAudioOutput : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly object _clockGate = new();
    private readonly float _sessionVolume;
    private double _clockOffsetSeconds;
    private bool _started;
    private bool _disposed;

    public WasapiAudioOutput(int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        WaveFormat = isFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);

        _buffer = new BufferedWaveProvider(WaveFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true
        };

        _output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
        _output.Init(_buffer);
        // Keep the volume selected by Windows for this audio session.  Do not
        // overwrite it with 1.0 when a video starts, otherwise every playback
        // would force the session back to 100% and sound much louder.
        _sessionVolume = Math.Clamp(_output.Volume, 0f, 1f);
        DeviceName = ResolveDefaultDeviceName();
        DiagnosticLog.Write("audio", $"AudioDevice={DeviceName} OutputFormat={WaveFormat.Encoding} {WaveFormat.SampleRate}Hz {WaveFormat.Channels}ch {WaveFormat.BitsPerSample}bit SessionVolume={_sessionVolume:0.###}");
    }

    public WaveFormat WaveFormat { get; }
    public string DeviceName { get; }
    public TimeSpan BufferedDuration => _buffer.BufferedDuration;
    public bool IsClockStarted => !_disposed && _started;

    public double PlaybackPositionSeconds
    {
        get
        {
            if (_disposed || !_started)
            {
                return Math.Max(0, _clockOffsetSeconds);
            }

            try
            {
                var rawSeconds = _output.GetPosition() / (double)Math.Max(1, WaveFormat.AverageBytesPerSecond);
                lock (_clockGate)
                {
                    return Math.Max(0, _clockOffsetSeconds + rawSeconds);
                }
            }
            catch
            {
                lock (_clockGate)
                {
                    return Math.Max(0, _clockOffsetSeconds);
                }
            }
        }
    }

    public void AddSamples(byte[] samples, int offset, int count)
    {
        if (_disposed || count <= 0)
        {
            return;
        }

        _buffer.AddSamples(samples, offset, count);
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        _output.Play();
        if (_started)
        {
            DiagnosticLog.Write("audio", "PlaybackResumed=true");
        }
        else
        {
            _started = true;
            DiagnosticLog.Write("audio", "PlaybackStarted=true");
        }
    }

    public void Pause()
    {
        if (_disposed || !_started)
        {
            return;
        }

        _output.Pause();
        DiagnosticLog.Write("audio", "PlaybackPaused=true");
    }

    public void ClearBuffer()
    {
        if (!_disposed)
        {
            _buffer.ClearBuffer();
        }
    }

    public void SetClockPosition(double mediaSeconds)
    {
        if (_disposed)
        {
            return;
        }

        double rawSeconds;
        try
        {
            rawSeconds = _output.GetPosition() / (double)Math.Max(1, WaveFormat.AverageBytesPerSecond);
        }
        catch
        {
            rawSeconds = 0;
        }

        lock (_clockGate)
        {
            _clockOffsetSeconds = mediaSeconds - rawSeconds;
        }
    }

    public void SetMuted(bool muted)
    {
        if (!_disposed)
        {
            _output.Volume = muted ? 0 : _sessionVolume;
        }
    }

    public void StopAndRelease()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _output.Stop();
        }
        finally
        {
            _buffer.ClearBuffer();
            _output.Dispose();
            _disposed = true;
            DiagnosticLog.Write("audio", "PlaybackStopped=true");
        }
    }

    public void Dispose() => StopAndRelease();

    private static string ResolveDefaultDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return string.IsNullOrWhiteSpace(device.FriendlyName) ? "Default WASAPI render endpoint" : device.FriendlyName;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("audio", $"AudioDevice=Default WASAPI render endpoint (name unavailable: {ex.Message})");
            return "Default WASAPI render endpoint";
        }
    }
}
