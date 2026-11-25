namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// A whole song decoded into memory and played wherever a reviewed play's playhead goes, at the rate
/// it moves: slower, faster or backwards, the pitch following like a record under a finger. The game
/// calls <see cref="Follow"/> every frame; the audio thread runs on at exactly that rate and jumps to the
/// playhead when it strays (after a seek, or drift), fading out when the playhead stands still.
/// </summary>
/// <remarks>ponytail: the PCM is kept whole (about 40 MB a stereo minute at 48 kHz float); stream it when that matters.</remarks>
public sealed class ScrubAudioSource : IAudioStreamSource
{
    private static readonly double Snap = 0.15; // seconds the sound may stray before it jumps to the playhead
    // Seconds the sound may stray on average (above the 20-40 ms a burst of rendering leads by) before it jumps.
    private static readonly double Drift = 0.05;
    private readonly float[] _pcm;
    private readonly int _channels;
    private readonly int _frames;
    private readonly object _gate = new();
    private double _targetFrame;
    private double _rate;
    private long _targetAt;
    private double _cursor = double.NaN;
    private float _gain;
    // The sound's distance from the playhead, averaged over about half a second (frames).
    private double _error;
    private readonly TimeProvider _time;
    // The time-stretcher: made on first use, off for good once it proves unavailable.
    private StretchHandle? _stretch;
    private bool _stretching, _stretchUnavailable, _keepPitch;

    private ScrubAudioSource(SdlAudioFormat format, float[] pcm, TimeProvider? time = null)
    {
        Format = format;
        _pcm = pcm;
        _channels = format.Channels;
        _frames = pcm.Length / _channels;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A song already decoded: interleaved <paramref name="pcm"/> in <paramref name="format"/>.</summary>
    public static ScrubAudioSource FromPcm(SdlAudioFormat format, float[] pcm, TimeProvider? time = null) => new(format, pcm, time);

    public SdlAudioFormat Format { get; }

    public bool IsCompleted => false;

    public Exception? Failure => null;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_frames / Format.SampleRate);

    /// <summary>Decodes the whole of <paramref name="input"/> (disposed afterwards) in the mixer's format.</summary>
    public static ScrubAudioSource Decode(Stream input, SdlAudioFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using (input)
        using (var decoder = NativeAudioDecoder.Open(input, checked((uint)format.SampleRate), checked((uint)format.Channels)))
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
    /// <paramref name="keepPitch"/>: play a speed up to 2x (training mode: 0.05x to 2x, jittering in the last digits) through the time-stretcher at the song's own pitch
    /// (training mode's slowed song); otherwise the pitch follows the speed, like a record (scrubbing).
    /// </summary>
    public void Follow(TimeSpan position, double rate, bool keepPitch = false)
    {
        lock (_gate)
        {
            _targetFrame = position.TotalSeconds * Format.SampleRate;
            _rate = double.IsFinite(rate) ? Math.Clamp(rate, -64, 64) : 0;
            _keepPitch = keepPitch;
            _targetAt = _time.GetTimestamp();
        }
    }

    public int Read(Span<float> interleavedDestination)
    {
        double target, rate;
        bool keepPitch;
        lock (_gate)
        {
            keepPitch = _keepPitch;
            // Where the playhead is by now, carried on from the last frame's report.
            target = _targetFrame + _rate * _time.GetElapsedTime(_targetAt).TotalSeconds * Format.SampleRate;
            rate = _rate;
        }
        var frames = interleavedDestination.Length / _channels;
        var jumped = false;
        if (double.IsNaN(_cursor) || Math.Abs(_cursor - target) > Snap * Format.SampleRate)
        {
            _cursor = target;
            _error = 0;
            jumped = true;
        }
        // Play at exactly the playhead's rate: any steering towards it is heard as wow (the mixer renders in bursts, a
        // whole queue refill at once, so each block alone strays by a queue's length and back). Only when the distance,
        // averaged over half a second, passes Drift does the sound jump to the playhead, fading in over 5 ms.
        _error += (target + rate * frames / 2 - _cursor - _error) * Math.Min(1, frames / (Format.SampleRate * 0.5));
        if (Math.Abs(_error) > Drift * Format.SampleRate)
        {
            _cursor += _error;
            _error = 0;
            _gain = 0;
            jumped = true;
        }
        var step = rate;
        var audible = Gain(rate);
        var ramp = 1f / (Format.SampleRate * 0.005f); // 5 ms
        // Speeds the caller wants at the song's pitch go through the time-stretcher (1x stays plain); a new speed carries
        // on in the running stretcher, only a jump restarts it.
        if (keepPitch && rate is > 0.02 and <= 2.01 && Math.Abs(rate - 1) > 1e-3
            && stretch(interleavedDestination[..(frames * _channels)], rate, restart: jumped || !_stretching))
        {
            _stretching = true;
            for (var frame = 0; frame < frames; frame++)
            {
                _gain += Math.Clamp(audible - _gain, -ramp, ramp);
                for (var channel = 0; channel < _channels; channel++)
                    interleavedDestination[frame * _channels + channel] *= _gain;
            }
            return frames * _channels;
        }
        _stretching = false;
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

    // Renders the block through the native time-stretcher from the cursor on; false when it is unavailable (a library
    // without it) or fails, and the caller resamples instead.
    private unsafe bool stretch(Span<float> destination, double rate, bool restart)
    {
        if (_stretchUnavailable)
            return false;
        try
        {
            _stretch ??= new StretchHandle(Format.SampleRate, _channels);
            var position = _cursor;
            int rendered;
            fixed (float* pcm = _pcm)
            fixed (float* output = destination)
                rendered = NativeMediaMethods.StretchRender(_stretch.Value, pcm, _frames, rate, restart ? 1 : 0, ref position,
                    output, destination.Length / _channels);
            if (rendered != destination.Length / _channels)
                throw new InvalidOperationException("The time-stretcher failed.");
            _cursor = position;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _stretchUnavailable = true;
            return false;
        }
    }

    // The native stretcher, freed with the song (the source is not disposable: the finalizer frees it).
    private sealed class StretchHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public StretchHandle(int sampleRate, int channels) : base(true)
        {
            SetHandle(NativeMediaMethods.StretchCreate(sampleRate, channels));
            if (IsInvalid)
                throw new InvalidOperationException("The time-stretcher could not be made.");
        }

        public nint Value => handle;

        protected override bool ReleaseHandle()
        {
            NativeMediaMethods.StretchDestroy(handle);
            return true;
        }
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
