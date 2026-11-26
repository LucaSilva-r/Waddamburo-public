namespace Waddamburo.Platform.Sdl.Media;

/// <summary>A song with sample-exact leading silence and a minimum transport duration.</summary>
public sealed class ScheduledAudioSource : IAudioStreamSource
{
    private readonly IAudioStreamSource _source;
    private readonly long _startFrame;
    private readonly long _minimumFrames;
    private long _position;
    private bool _ended;
    private Exception? _failure;

    public ScheduledAudioSource(IAudioStreamSource source, TimeSpan start, TimeSpan minimumDuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDuration, TimeSpan.Zero);
        _source = source;
        _startFrame = checked((long)Math.Round(start.TotalSeconds * Format.SampleRate));
        _minimumFrames = checked((long)Math.Ceiling(minimumDuration.TotalSeconds * Format.SampleRate));
    }

    public SdlAudioFormat Format => _source.Format;
    public bool IsCompleted => _ended;
    public Exception? Failure => _failure ?? _source.Failure;
    public long ProducedFrames => Interlocked.Read(ref _position);

    /// <summary>
    /// Read ahead of the output (device voices): a read the decoder cannot fill yet just returns less, the rest
    /// follows in order. Read at the output's pace instead, a short read is a gap the song cannot recover from.
    /// </summary>
    internal bool Gapless { get; set; }

    public int Read(Span<float> interleavedDestination)
    {
        var destination = interleavedDestination;
        if (destination.Length % Format.Channels != 0)
            throw new ArgumentException("Whole audio frames are required.", nameof(interleavedDestination));
        if (_ended)
            return 0;
        destination.Clear();
        var frames = destination.Length / Format.Channels;
        var prefix = (int)Math.Min(frames, Math.Max(0, _startFrame - _position));
        var read = 0;
        if (prefix < frames && !_source.IsCompleted)
        {
            var samples = _source.Read(destination[(prefix * Format.Channels)..]);
            if (samples < 0 || samples > destination.Length - prefix * Format.Channels || samples % Format.Channels != 0)
                throw new InvalidDataException("The song decoder returned an invalid frame count.");
            read = samples / Format.Channels;
            if (read < frames - prefix && !_source.IsCompleted)
            {
                if (Gapless)
                {
                    Interlocked.Add(ref _position, prefix + read);
                    return (prefix + read) * Format.Channels;
                }
                _failure = new IOException("Gameplay audio underrun; playback cannot continue in sync.");
            }
        }
        if (Failure is not null)
            _ended = true;
        var count = prefix + read;
        if (!_ended)
            count = (int)Math.Max(count, Math.Min(frames, Math.Max(0, _minimumFrames - _position)));
        Interlocked.Add(ref _position, count);
        _ended |= _source.IsCompleted && _position >= _minimumFrames && _position >= _startFrame;
        return count * Format.Channels;
    }

    public void Dispose() => _source.Dispose();
}
