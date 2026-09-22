namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Retains position and failure information after the mixer releases a stream.</summary>
public sealed class AudioStreamTransport : IAudioStreamSource
{
    private readonly ScheduledAudioSource _source;
    private readonly Func<ulong> _submittedFrames;
    private long _firstOutputFrame = -1;
    private long _lastPosition;

    internal AudioStreamTransport(ScheduledAudioSource source, Func<ulong> submittedFrames)
    {
        _source = source;
        _submittedFrames = submittedFrames;
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
        if (_firstOutputFrame < 0)
            return TimeSpan.Zero;
        var latencyFrames = checked((long)Math.Ceiling(hardwareLatency.TotalSeconds * Format.SampleRate));
        var played = checked((long)submitted) - checked((long)queued) - latencyFrames - _firstOutputFrame;
        _lastPosition = Math.Max(_lastPosition, Math.Clamp(played, 0, _source.ProducedFrames));
        return TimeSpan.FromSeconds((double)_lastPosition / Format.SampleRate);
    }

    public void Dispose() => _source.Dispose();
}
