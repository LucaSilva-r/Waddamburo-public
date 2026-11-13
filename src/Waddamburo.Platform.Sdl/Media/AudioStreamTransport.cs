namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Retains position and failure information after the mixer releases a stream.</summary>
public sealed class AudioStreamTransport : IAudioStreamSource
{
    private readonly ScheduledAudioSource _source;
    private readonly Func<ulong> _submittedFrames;
    private long _firstOutputFrame = -1;
    private long _lastPosition;
    private readonly AudioPlaybackClock _clock = new();
    private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
    private bool _paused;
    private ulong _pausedAt;

    private readonly Func<string> _deviceDiagnostics;

    internal AudioStreamTransport(ScheduledAudioSource source, Func<ulong> submittedFrames, Func<string>? deviceDiagnostics = null)
    {
        _source = source;
        _submittedFrames = submittedFrames;
        _deviceDiagnostics = deviceDiagnostics ?? (static () => "");
    }

    public AudioPlaybackHandle Handle { get; internal set; }
    public SdlAudioFormat Format => _source.Format;
    public bool IsCompleted => _source.IsCompleted;
    public Exception? Failure => _source.Failure;

    public int Read(Span<float> interleavedDestination)
    {
        if (_firstOutputFrame < 0)
            _firstOutputFrame = checked((long)_submittedFrames());
        return _source.Read(interleavedDestination);
    }

    internal TimeSpan Position(ulong submitted, ulong queued, TimeSpan hardwareLatency)
    {
        if (_paused)
            return TimeSpan.FromSeconds((double)_lastPosition / Format.SampleRate);
        if (_firstOutputFrame < 0)
            return TimeSpan.Zero;
        var consumed = checked((long)submitted) - checked((long)queued) - _firstOutputFrame;
        var position = _clock.Update(TimeSpan.FromSeconds((double)consumed / Format.SampleRate),
            hardwareLatency, _elapsed.Elapsed);
        var played = checked((long)Math.Floor(position.TotalSeconds * Format.SampleRate));
        var produced = _source.ProducedFrames;
        // Not held to the frames mixed so far while the song plays: WASAPI exclusive mixes one 3 ms period ahead,
        // so after any late period the clock sat on that bound and the notes moved in period-sized steps.
        _lastPosition = Math.Max(_lastPosition, Math.Clamp(played, 0, _source.IsCompleted ? produced : long.MaxValue));
        diagnose(played > produced, consumed);
        return TimeSpan.FromSeconds((double)_lastPosition / Format.SampleRate);
    }

    // Diagnostics (the 2026-11 Windows note-lane stutter): every 5 s a line when the clock misbehaved (it ran ahead
    // of the mixed audio, or a read stepped 2 ms off the wall clock), every 5 s regardless with WADDAMBURO_HITCH_TRACE=1.
    private static readonly bool Trace = Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";
    private long _windowStartTicks = -1, _windowStartPosition, _windowStartConsumed, _lastCallTicks, _lastCallPosition;
    private int _calls, _capped;
    private double _worstStepError;

    private void diagnose(bool capped, long consumed)
    {
        var now = _elapsed.Elapsed.Ticks;
        if (_windowStartTicks < 0)
        {
            (_windowStartTicks, _windowStartPosition, _windowStartConsumed) = (now, _lastPosition, consumed);
            (_lastCallTicks, _lastCallPosition) = (now, _lastPosition);
            return;
        }
        _calls++;
        if (capped)
            _capped++;
        // How far this call's step strays from the wall clock's: what the eye sees as a stutter.
        var stepError = Math.Abs((double)(_lastPosition - _lastCallPosition) / Format.SampleRate
            - TimeSpan.FromTicks(now - _lastCallTicks).TotalSeconds) * 1000;
        _worstStepError = Math.Max(_worstStepError, stepError);
        (_lastCallTicks, _lastCallPosition) = (now, _lastPosition);
        var wall = TimeSpan.FromTicks(now - _windowStartTicks).TotalSeconds;
        if (wall < 5)
            return;
        var rate = (double)Format.SampleRate;
        var device = _deviceDiagnostics(); // read every window: its counters restart
        if (Trace || _capped > 0 || _worstStepError > 2)
            Console.WriteLine(FormattableString.Invariant(
            $"Audio clock: song {_lastPosition / rate:F1} s, clock speed {(_lastPosition - _windowStartPosition) / rate / wall * 100:F2}%, ")
            + FormattableString.Invariant(
                $"audio speed {(consumed - _windowStartConsumed) / rate / wall * 100:F2}%, clock minus audio {(_lastPosition - consumed) * 1000 / rate:F1} ms, ")
            + FormattableString.Invariant($"ahead of mix {_capped}/{_calls} reads, worst step error {_worstStepError:F1} ms; {device}"));
        (_windowStartTicks, _windowStartPosition, _windowStartConsumed) = (now, _lastPosition, consumed);
        (_calls, _capped, _worstStepError) = (0, 0, 0);
    }

    // ponytail: the song frames already queued when pausing still play out (~21 ms at the default
    // buffer) and are skipped on resume; hold them back in the device if that ever matters.
    internal void SetPaused(bool paused, ulong submitted)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused)
        {
            _pausedAt = submitted;
            _elapsed.Stop();
        }
        else
        {
            // Frames submitted while paused held no song: start the song that much later.
            if (_firstOutputFrame >= 0)
                _firstOutputFrame += checked((long)(submitted - _pausedAt));
            _elapsed.Start();
        }
    }

    public void Dispose() => _source.Dispose();
}
