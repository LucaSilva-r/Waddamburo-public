namespace Waddamburo.Platform.Sdl.Media;

public enum AudioBus
{
    Bgm,
    Preview,
    MenuSound,
    Voice,
    DrumHit,
}

public readonly record struct AudioPlaybackHandle(long Value);

/// <summary>Bounded, device-format PCM intended for jingles and sound effects.</summary>
public sealed class AudioClip
{
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumPermittedDuration = TimeSpan.FromMinutes(2);
    private readonly float[] _samples;

    public AudioClip(SdlAudioFormat format, ReadOnlySpan<float> interleavedSamples)
        : this(format, interleavedSamples.ToArray())
    {
    }

    private AudioClip(SdlAudioFormat format, float[] interleavedSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(format.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(format.Channels);
        if (interleavedSamples.Length == 0 || interleavedSamples.Length % format.Channels != 0)
        {
            throw new ArgumentException(
                "A clip must contain a whole, non-empty number of interleaved frames.",
                nameof(interleavedSamples));
        }
        Format = format;
        _samples = interleavedSamples;
    }

    public SdlAudioFormat Format { get; }

    public int FrameCount => _samples.Length / Format.Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Format.SampleRate);

    public static AudioClip Load(
        string path,
        SdlAudioFormat format,
        TimeSpan? maximumDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var durationLimit = maximumDuration ?? DefaultMaximumDuration;
        if (durationLimit <= TimeSpan.Zero || durationLimit > MaximumPermittedDuration)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        var maximumFrames = checked((ulong)Math.Ceiling(durationLimit.TotalSeconds * format.SampleRate));
        using var decoder = new NativeAudioDecoder(
            path,
            checked((uint)format.SampleRate),
            checked((uint)format.Channels));
        if (decoder.Info.TotalFrames is ulong reportedFrames && reportedFrames > maximumFrames)
        {
            throw new InvalidDataException(
                $"The {reportedFrames}-frame audio file exceeds the {maximumFrames}-frame one-shot limit.");
        }

        var capacityFrames = decoder.Info.TotalFrames is ulong knownFrames
            ? Math.Min(knownFrames, maximumFrames)
            : Math.Min(maximumFrames, (ulong)(format.SampleRate * 4));
        var samples = new List<float>(checked((int)Math.Min(
            capacityFrames * (ulong)format.Channels,
            int.MaxValue)));
        var buffer = new float[4096 * format.Channels];
        ulong decodedFrames = 0;
        for (;;)
        {
            var sampleCount = decoder.Read(buffer);
            if (sampleCount == 0)
                break;
            decodedFrames += checked((ulong)(sampleCount / format.Channels));
            if (decodedFrames > maximumFrames)
            {
                throw new InvalidDataException(
                    $"Decoded audio exceeds the {maximumFrames}-frame one-shot limit.");
            }
            samples.AddRange(buffer.AsSpan(0, sampleCount));
        }
        if (samples.Count == 0)
            throw new InvalidDataException("The audio clip contains no decoded frames.");
        return new AudioClip(format, [.. samples]);
    }

    internal ReadOnlySpan<float> Samples => _samples;
}

/// <summary>Thread-safe software mixer whose output format is fixed at construction.</summary>
public sealed class AudioMixer
{
    private readonly object _gate = new();
    private readonly BusState[] _buses = new BusState[Enum.GetValues<AudioBus>().Length];
    private readonly List<Voice> _voices = [];
    private long _nextHandle;
    private float _masterVolume = 1f;

    public AudioMixer(SdlAudioFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(format.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(format.Channels);
        Format = format;
        for (var index = 0; index < _buses.Length; index++)
            _buses[index] = new BusState();
    }

    public SdlAudioFormat Format { get; }

    public bool HasActiveVoices
    {
        get
        {
            lock (_gate)
                return _voices.Count != 0;
        }
    }

    public float MasterVolume
    {
        get
        {
            lock (_gate)
                return _masterVolume;
        }
        set
        {
            validateVolume(value, nameof(value));
            lock (_gate)
                _masterVolume = value;
        }
    }

    public AudioPlaybackHandle Play(
        AudioClip clip,
        AudioBus bus,
        float volume = 1f,
        bool loop = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        validateBus(bus);
        validateVolume(volume, nameof(volume));
        if (clip.Format != Format)
            throw new ArgumentException("The clip format does not match the mixer format.", nameof(clip));
        lock (_gate)
        {
            var handle = new AudioPlaybackHandle(checked(++_nextHandle));
            _voices.Add(new Voice(handle, clip, bus, volume, loop));
            return handle;
        }
    }

    public void Stop(AudioPlaybackHandle handle, TimeSpan fadeDuration = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fadeDuration, TimeSpan.Zero);
        lock (_gate)
        {
            var voice = _voices.Find(candidate => candidate.Handle == handle);
            if (voice is null)
                return;
            var fadeFrames = checked((long)Math.Ceiling(fadeDuration.TotalSeconds * Format.SampleRate));
            if (fadeFrames == 0)
                _voices.Remove(voice);
            else
                voice.BeginFade(fadeFrames);
        }
    }

    public void StopBus(AudioBus bus, TimeSpan fadeDuration = default)
    {
        validateBus(bus);
        ArgumentOutOfRangeException.ThrowIfLessThan(fadeDuration, TimeSpan.Zero);
        lock (_gate)
        {
            var fadeFrames = checked((long)Math.Ceiling(fadeDuration.TotalSeconds * Format.SampleRate));
            if (fadeFrames == 0)
                _voices.RemoveAll(voice => voice.Bus == bus);
            else
            {
                foreach (var voice in _voices.Where(voice => voice.Bus == bus))
                    voice.BeginFade(fadeFrames);
            }
        }
    }

    public void SetBusVolume(AudioBus bus, float volume)
    {
        validateBus(bus);
        validateVolume(volume, nameof(volume));
        lock (_gate)
            _buses[(int)bus].Volume = volume;
    }

    public void SetBusMuted(AudioBus bus, bool muted)
    {
        validateBus(bus);
        lock (_gate)
            _buses[(int)bus].Muted = muted;
    }

    /// <summary>Fills the destination and returns whether a voice contributed or advanced.</summary>
    public bool Render(Span<float> interleavedDestination)
    {
        if (interleavedDestination.Length == 0 || interleavedDestination.Length % Format.Channels != 0)
        {
            throw new ArgumentException(
                "The destination must hold a whole, non-empty number of interleaved frames.",
                nameof(interleavedDestination));
        }
        interleavedDestination.Clear();
        lock (_gate)
        {
            var hadVoices = _voices.Count != 0;
            var frameCount = interleavedDestination.Length / Format.Channels;
            for (var voiceIndex = _voices.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var voice = _voices[voiceIndex];
                var bus = _buses[(int)voice.Bus];
                for (var outputFrame = 0; outputFrame < frameCount; outputFrame++)
                {
                    if (voice.Position >= voice.Clip.FrameCount)
                    {
                        if (!voice.Loop)
                            break;
                        voice.Position = 0;
                    }

                    var fade = voice.FadeFramesTotal == 0
                        ? 1f
                        : (float)voice.FadeFramesRemaining / voice.FadeFramesTotal;
                    var gain = bus.Muted ? 0f : _masterVolume * bus.Volume * voice.Volume * fade;
                    var sourceOffset = voice.Position * Format.Channels;
                    var outputOffset = outputFrame * Format.Channels;
                    for (var channel = 0; channel < Format.Channels; channel++)
                    {
                        interleavedDestination[outputOffset + channel] +=
                            voice.Clip.Samples[sourceOffset + channel] * gain;
                    }
                    voice.Position++;
                    if (voice.FadeFramesRemaining > 0 && --voice.FadeFramesRemaining == 0)
                        break;
                }
                if ((!voice.Loop && voice.Position >= voice.Clip.FrameCount) ||
                    (voice.FadeFramesTotal != 0 && voice.FadeFramesRemaining == 0))
                {
                    _voices.RemoveAt(voiceIndex);
                }
            }
            for (var index = 0; index < interleavedDestination.Length; index++)
                interleavedDestination[index] = Math.Clamp(interleavedDestination[index], -1f, 1f);
            return hadVoices;
        }
    }

    private void validateBus(AudioBus bus)
    {
        if ((uint)bus >= (uint)_buses.Length)
            throw new ArgumentOutOfRangeException(nameof(bus));
    }

    private static void validateVolume(float volume, string parameterName)
    {
        if (!float.IsFinite(volume) || volume is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(parameterName, "Volume must be between zero and one.");
    }

    private sealed class BusState
    {
        public float Volume { get; set; } = 1f;

        public bool Muted { get; set; }
    }

    private sealed class Voice(
        AudioPlaybackHandle handle,
        AudioClip clip,
        AudioBus bus,
        float volume,
        bool loop)
    {
        public AudioPlaybackHandle Handle { get; } = handle;

        public AudioClip Clip { get; } = clip;

        public AudioBus Bus { get; } = bus;

        public float Volume { get; } = volume;

        public bool Loop { get; } = loop;

        public int Position { get; set; }

        public long FadeFramesTotal { get; private set; }

        public long FadeFramesRemaining { get; set; }

        public void BeginFade(long frames)
        {
            if (FadeFramesRemaining != 0 && FadeFramesRemaining <= frames)
                return;
            FadeFramesTotal = frames;
            FadeFramesRemaining = frames;
        }
    }
}
