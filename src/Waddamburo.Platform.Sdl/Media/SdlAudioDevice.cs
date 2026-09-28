using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>The fixed application-side format accepted by an SDL playback device.</summary>
public readonly record struct SdlAudioFormat(int SampleRate, int Channels);

/// <summary>
/// Owns one SDL logical playback device and its application-side float stream.
/// Producers may queue audio from any thread; the owner must outlive all producers.
/// </summary>
public sealed unsafe class SdlAudioDevice : IAudioOutput
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 2;

    private readonly object _gate = new();
    private SDL_AudioStream* _stream;
    private SDL_AudioDeviceID _device;
    private bool _audioInitialized;
    private bool _disposed;
    private ulong _submittedFrames;
    private readonly List<DirectVoice> _directVoices = [];

    public SdlAudioDevice(
        int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels,
        int requestedBufferFrames = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (channels > 8)
            throw new ArgumentOutOfRangeException(nameof(channels), "At most eight output channels are supported.");

        Format = new SdlAudioFormat(sampleRate, channels);
        // Request a smaller device period before opening it. SDL and the backend may
        // choose another size; an explicit user setting takes precedence.
        if (Environment.GetEnvironmentVariable("SDL_AUDIO_DEVICE_SAMPLE_FRAMES") is null)
            SDL_SetHint("SDL_AUDIO_DEVICE_SAMPLE_FRAMES",
                requestedBufferFrames.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
            throw sdlFailure("initialize SDL audio");
        _audioInitialized = true;
        try
        {
            var specification = new SDL_AudioSpec
            {
                format = SDL_AudioFormat.SDL_AUDIO_F32LE,
                channels = channels,
                freq = sampleRate,
            };
            // Opened as a device (not SDL_OpenAudioDeviceStream) so direct voices can bind streams to it too.
            _device = SDL_OpenAudioDevice(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &specification);
            if (_device == 0)
                throw sdlFailure("open the SDL playback device");
            if (!SDL_PauseAudioDevice(_device))
                throw sdlFailure("pause SDL audio playback");
            _stream = SDL_CreateAudioStream(&specification, null);
            if (_stream is null || !SDL_BindAudioStream(_device, _stream))
                throw sdlFailure("create the SDL playback stream");

            SDL_AudioSpec hardwareSpecification;
            int hardwareBufferFrames;
            if (!SDL_GetAudioDeviceFormat(_device, &hardwareSpecification, &hardwareBufferFrames))
            {
                throw sdlFailure("query the SDL playback device format");
            }
            HardwareFormat = new SdlAudioFormat(
                hardwareSpecification.freq,
                hardwareSpecification.channels);
            HardwareBufferFrames = hardwareBufferFrames;
            Driver = SDL_GetCurrentAudioDriver() ?? "unknown";
        }
        catch
        {
            disposeNativeResources();
            throw;
        }
    }

    /// <summary>The format callers put into this device. Samples are interleaved floats.</summary>
    public SdlAudioFormat Format { get; }

    /// <summary>The physical format selected by SDL; SDL converts from <see cref="Format"/>.</summary>
    public SdlAudioFormat HardwareFormat { get; }

    public int HardwareBufferFrames { get; }

    public string Driver { get; } = string.Empty;

    /// <summary>Total frames accepted since this device was opened, including frames later cleared.</summary>
    public ulong SubmittedFrames
    {
        get
        {
            lock (_gate)
                return _submittedFrames;
        }
    }

    /// <summary>Frames still waiting on the application side of SDL's stream.</summary>
    public ulong QueuedFrames
    {
        get
        {
            lock (_gate)
            {
                ensureUsable();
                var bytes = SDL_GetAudioStreamQueued(_stream);
                if (bytes < 0)
                    throw sdlFailure("query queued SDL audio");
                return (ulong)bytes / bytesPerFrame;
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ensureUsable();
            if (!SDL_ResumeAudioDevice(_device))
                throw sdlFailure("resume SDL audio playback");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ensureUsable();
            if (!SDL_PauseAudioDevice(_device))
                throw sdlFailure("pause SDL audio playback");
        }
    }

    public void Queue(ReadOnlySpan<float> interleavedSamples)
    {
        if (interleavedSamples.Length % Format.Channels != 0)
        {
            throw new ArgumentException(
                "The source must contain a whole number of interleaved frames.",
                nameof(interleavedSamples));
        }
        var byteCount = checked(interleavedSamples.Length * sizeof(float));
        lock (_gate)
        {
            ensureUsable();
            if (interleavedSamples.IsEmpty)
                return;
            fixed (float* source = interleavedSamples)
            {
                if (!SDL_PutAudioStreamData(_stream, (nint)source, byteCount))
                    throw sdlFailure("queue SDL audio");
            }
            _submittedFrames += checked((ulong)(interleavedSamples.Length / Format.Channels));
        }
    }

    /// <summary>
    /// A stream of its own bound to the device: SDL mixes it in on its audio thread at the next device
    /// buffer, past the application-side queue, and a managed GC pause cannot hold that thread.
    /// </summary>
    public DirectVoice CreateDirectVoice()
    {
        lock (_gate)
        {
            ensureUsable();
            var specification = new SDL_AudioSpec
            {
                format = SDL_AudioFormat.SDL_AUDIO_F32LE,
                channels = Format.Channels,
                freq = Format.SampleRate,
            };
            var stream = SDL_CreateAudioStream(&specification, &specification);
            if (stream is null)
                throw sdlFailure("create an SDL voice stream");
            if (!SDL_BindAudioStream(_device, stream))
            {
                SDL_DestroyAudioStream(stream);
                throw sdlFailure("bind an SDL voice stream");
            }
            var voice = new DirectVoice(stream, Format.Channels);
            _directVoices.Add(voice);
            return voice;
        }
    }

    /// <summary>Marks all currently queued source data as complete for SDL conversion.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            ensureUsable();
            if (!SDL_FlushAudioStream(_stream))
                throw sdlFailure("flush SDL audio");
        }
    }

    /// <summary>Discards queued samples without changing the submitted-frame counter.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ensureUsable();
            if (!SDL_ClearAudioStream(_stream))
                throw sdlFailure("clear queued SDL audio");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            disposeNativeResources();
        }
    }

    private ulong bytesPerFrame => checked((ulong)Format.Channels * sizeof(float));

    private void ensureUsable()
        => ObjectDisposedException.ThrowIf(_disposed || _stream is null, this);

    private void disposeNativeResources()
    {
        foreach (var voice in _directVoices)
            voice.Destroy();
        _directVoices.Clear();
        if (_stream is not null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
        if (_device != 0)
        {
            SDL_CloseAudioDevice(_device);
            _device = 0;
        }
        if (_audioInitialized)
        {
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            _audioInitialized = false;
        }
    }

    private static InvalidOperationException sdlFailure(string operation)
        => new($"Failed to {operation}: {SDL_GetError()}");
}

/// <summary>One sound at a time, mixed by SDL into the device (see <see cref="SdlAudioDevice.CreateDirectVoice"/>).</summary>
public sealed unsafe class DirectVoice
{
    private SDL_AudioStream* _stream;
    private readonly int _channels;
    // What was last put into the stream, and a spare to build the next one in (no garbage per hit).
    private float[] _queued = [];
    private int _queuedLength;
    private float[] _spare = [];

    internal DirectVoice(SDL_AudioStream* stream, int channels)
    {
        _stream = stream;
        _channels = channels;
    }

    public bool IsPlaying => _stream is not null && SDL_GetAudioStreamQueued(_stream) > 0;

    /// <summary>Starts <paramref name="samples"/> times <paramref name="gain"/> at the next device buffer, over what is left of the previous sound.</summary>
    public void Play(ReadOnlySpan<float> samples, float gain) => replace(samples, gain, fadeFrames: -1);

    /// <summary>Ends the sound, fading what is left of it out over <paramref name="fadeFrames"/> (0 = cut).</summary>
    public void Stop(int fadeFrames) => replace([], 0f, fadeFrames);

    private void replace(ReadOnlySpan<float> samples, float gain, int fadeFrames)
    {
        if (_stream is null)
            return;
        SDL_LockAudioStream(_stream);
        try
        {
            var queued = Math.Min(_queuedLength, SDL_GetAudioStreamQueued(_stream) / sizeof(float));
            queued -= queued % _channels;
            var tail = _queued.AsSpan(_queuedLength - queued, queued);
            if (fadeFrames >= 0)
                tail = tail[..Math.Min(tail.Length, fadeFrames * _channels)];
            var length = Math.Max(samples.Length, tail.Length);
            if (_spare.Length < length)
                _spare = new float[length];
            var next = _spare.AsSpan(0, length);
            next.Clear();
            for (var index = 0; index < samples.Length; index++)
                next[index] = samples[index] * gain;
            for (var index = 0; index < tail.Length; index++)
                next[index] += fadeFrames > 0 ? tail[index] * (1f - (float)(index / _channels) / fadeFrames) : tail[index];
            SDL_ClearAudioStream(_stream);
            if (length != 0)
                fixed (float* source = next)
                    SDL_PutAudioStreamData(_stream, (nint)source, length * sizeof(float));
            (_queued, _spare) = (_spare, _queued);
            _queuedLength = length;
        }
        finally
        {
            SDL_UnlockAudioStream(_stream);
        }
    }

    internal void Destroy()
    {
        if (_stream is null)
            return;
        SDL_DestroyAudioStream(_stream);
        _stream = null;
    }
}
