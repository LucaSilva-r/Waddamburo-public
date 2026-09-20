using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>The fixed application-side format accepted by an SDL playback device.</summary>
public readonly record struct SdlAudioFormat(int SampleRate, int Channels);

/// <summary>
/// Owns one SDL logical playback device and its application-side float stream.
/// Producers may queue audio from any thread; the owner must outlive all producers.
/// </summary>
public sealed unsafe class SdlAudioDevice : IDisposable
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 2;

    private readonly object _gate = new();
    private SDL_AudioStream* _stream;
    private bool _audioInitialized;
    private bool _disposed;
    private ulong _submittedFrames;

    public SdlAudioDevice(
        int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (channels > 8)
            throw new ArgumentOutOfRangeException(nameof(channels), "At most eight output channels are supported.");

        Format = new SdlAudioFormat(sampleRate, channels);
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
            _stream = SDL_OpenAudioDeviceStream(
                SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
                &specification,
                null,
                0);
            if (_stream is null)
                throw sdlFailure("open the SDL playback device");

            var device = SDL_GetAudioStreamDevice(_stream);
            SDL_AudioSpec hardwareSpecification;
            int hardwareBufferFrames;
            if (device == 0 ||
                !SDL_GetAudioDeviceFormat(device, &hardwareSpecification, &hardwareBufferFrames))
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
            if (!SDL_ResumeAudioStreamDevice(_stream))
                throw sdlFailure("resume SDL audio playback");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ensureUsable();
            if (!SDL_PauseAudioStreamDevice(_stream))
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
        if (_stream is not null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
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
