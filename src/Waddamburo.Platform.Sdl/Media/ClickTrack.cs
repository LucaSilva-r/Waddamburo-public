namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// An endless metronome for audio calibration: <paramref name="accent"/> on the first beat of every bar,
/// <paramref name="beat"/> on the others (BIP bap bap bap), mixed sample-exact from frame 0 and silent
/// until <see cref="SetAudibleFrom"/>. Played as a transport, its position is the same clock songs use.
/// </summary>
public sealed class ClickTrack(SdlAudioFormat format, TimeSpan interval, AudioClip accent, AudioClip beat,
    int beatsPerBar = 4) : IAudioStreamSource
{
    private readonly long _beatFrames = (long)Math.Round(interval.TotalSeconds * format.SampleRate);
    private readonly long _longestClip = Math.Max(accent.FrameCount, beat.FrameCount);
    private long _position;
    private long _audibleFrom = long.MaxValue;

    public SdlAudioFormat Format => format;
    public bool IsCompleted => false;
    public Exception? Failure => null;

    /// <summary>Beats sound from this track time on (a beat already partly played stays silent).</summary>
    public void SetAudibleFrom(TimeSpan time) =>
        Interlocked.Exchange(ref _audibleFrom, (long)Math.Round(time.TotalSeconds * format.SampleRate));

    /// <summary>A short tone with an instant attack (the onset is the beat), for when no drum sound is at hand.</summary>
    public static AudioClip Tone(SdlAudioFormat format, double hertz)
    {
        var frames = format.SampleRate / 25; // 40 ms
        var samples = new float[frames * format.Channels];
        for (var frame = 0; frame < frames; frame++)
            samples.AsSpan(frame * format.Channels, format.Channels).Fill((float)(0.7
                * Math.Sin(2 * Math.PI * hertz * frame / format.SampleRate) * Math.Exp(-frame / (0.006 * format.SampleRate))));
        return new AudioClip(format, samples);
    }

    public int Read(Span<float> interleavedDestination)
    {
        var channels = format.Channels;
        var frames = interleavedDestination.Length / channels;
        interleavedDestination.Clear();
        var audibleFrom = Interlocked.Read(ref _audibleFrom);
        var end = _position + frames;
        // Every beat whose sound overlaps this block (a long sound runs into the next beats').
        for (var index = Math.Max(0, (_position - _longestClip) / _beatFrames); index * _beatFrames < end; index++)
        {
            var start = index * _beatFrames;
            if (start < audibleFrom)
                continue;
            var clip = index % beatsPerBar == 0 ? accent : beat;
            var from = Math.Max(start, _position);
            var to = Math.Min(start + clip.FrameCount, end);
            if (from >= to)
                continue;
            var source = clip.Samples.Slice((int)(from - start) * channels, (int)(to - from) * channels);
            var destination = interleavedDestination.Slice((int)(from - _position) * channels, source.Length);
            for (var sample = 0; sample < source.Length; sample++)
                destination[sample] += source[sample];
        }
        _position = end;
        return frames * channels;
    }

    public void Dispose()
    {
    }
}
