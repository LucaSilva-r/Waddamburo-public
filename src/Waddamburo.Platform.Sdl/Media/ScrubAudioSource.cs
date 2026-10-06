using System.Diagnostics;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// A whole song decoded into memory and played wherever a reviewed play's playhead goes, at the rate
/// it moves: slower, faster or backwards, the pitch following like a record under a finger. The game
/// calls <see cref="Follow"/> every frame; the audio thread runs on at that rate and leans towards the
/// playhead (snapping to it after a jump), fading out when the playhead stands still.
/// </summary>
/// <remarks>ponytail: the PCM is kept whole (about 40 MB a stereo minute at 48 kHz float); stream it when that matters.</remarks>
public sealed class ScrubAudioSource : IAudioStreamSource
{
    private static readonly double Snap = 0.15; // seconds the sound may stray before it jumps to the playhead
    private readonly float[] _pcm;
    private readonly int _channels;
    private readonly int _frames;
    private readonly object _gate = new();
    private double _targetFrame;
    private double _rate;
    private long _targetAt;
    private double _cursor = double.NaN;
    private float _gain;

    private ScrubAudioSource(SdlAudioFormat format, float[] pcm)
    {
        Format = format;
        _pcm = pcm;
        _channels = format.Channels;
        _frames = pcm.Length / _channels;
    }

    public SdlAudioFormat Format { get; }

    public bool IsCompleted => false;

    public Exception? Failure => null;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_frames / Format.SampleRate);

    /// <summary>Decodes the whole of <paramref name="input"/> (disposed afterwards) in the mixer's format.</summary>
    public static ScrubAudioSource Decode(Stream input, SdlAudioFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using (input)
        using (var decoder = new NativeAudioDecoder(input, checked((uint)format.SampleRate), checked((uint)format.Channels)))
        {
            var samples = new List<float>(format.SampleRate * format.Channels * 60 * 4);
            var block = new float[4096 * format.Channels];
            int read;
            while ((read = decoder.Read(block)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.AddRange(block.AsSpan(0, read));
            }
            return new ScrubAudioSource(format, [.. samples]);
        }
    }

    /// <summary>
    /// Where the playhead is in the song (<paramref name="position"/>; before 0 or past the end is
    /// silence) and how fast it moves, in song seconds per real second (negative: backwards).
    /// </summary>
    public void Follow(TimeSpan position, double rate)
    {
        lock (_gate)
        {
            _targetFrame = position.TotalSeconds * Format.SampleRate;
            _rate = double.IsFinite(rate) ? Math.Clamp(rate, -64, 64) : 0;
            _targetAt = Stopwatch.GetTimestamp();
        }
    }

    public int Read(Span<float> interleavedDestination)
    {
        double target, rate;
        lock (_gate)
        {
            // Where the playhead is by now, carried on from the last frame's report.
            target = _targetFrame + _rate * Stopwatch.GetElapsedTime(_targetAt).TotalSeconds * Format.SampleRate;
            rate = _rate;
        }
        var frames = interleavedDestination.Length / _channels;
        if (double.IsNaN(_cursor) || Math.Abs(_cursor - target) > Snap * Format.SampleRate)
            _cursor = target;
        // Lean towards the playhead over about a tenth of a second.
        var correction = (target + rate * frames / 2 - _cursor) / (Format.SampleRate * 0.1);
        var step = rate + Math.Clamp(correction, -0.5, 0.5);
        var audible = Gain(rate);
        var ramp = 1f / (Format.SampleRate * 0.005f); // 5 ms
        for (var frame = 0; frame < frames; frame++)
        {
            _gain += Math.Clamp(audible - _gain, -ramp, ramp);
            var position = _cursor;
            var whole = (int)Math.Floor(position);
            var fraction = (float)(position - whole);
            for (var channel = 0; channel < _channels; channel++)
            {
                var sample = (sampleAt(whole, channel) * (1 - fraction) + sampleAt(whole + 1, channel) * fraction) * _gain;
                interleavedDestination[frame * _channels + channel] = sample;
            }
            _cursor += step;
        }
        return frames * _channels;
    }

    /// <summary>
    /// The volume at a rate: silent standing still, full up to 2x, then quieter as a scrub speeds up
    /// (a fast scrub's chatter is noise): half at 8x, a fifth at 40x.
    /// </summary>
    public static float Gain(double rate)
    {
        var speed = Math.Abs(rate);
        return speed <= 0.02 ? 0 : speed <= 2 ? 1 : (float)Math.Max(0.2, Math.Sqrt(2 / speed));
    }

    private float sampleAt(int frame, int channel) =>
        frame >= 0 && frame < _frames ? _pcm[frame * _channels + channel] : 0;

    public void Dispose()
    {
    }
}
