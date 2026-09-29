using System.Diagnostics;

namespace Waddamburo.Platform.Sdl.Media;

public readonly record struct AudioPerformanceCounters(long Blocks, long WorkTicks);

/// <summary>
/// Feeds mixed PCM to one device: an SDL device from a bounded background producer, or a WASAPI
/// exclusive device that pulls each buffer from the mixer on its own thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int RenderFrames = 128;
    private const int TargetQueuedFrames = 512;

    private readonly IAudioOutput _device;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread? _producer;
    private readonly object _outputGate = new();
    private volatile bool _performanceMonitoring;
    private long _performanceBlocks;
    private long _performanceWorkTicks;
    private bool _disposed;

    public AudioEngine(IAudioOutput device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Mixer = new AudioMixer(device.Format);
        if (device is SdlAudioDevice sdl)
        {
            // The mix keeps a queue (it rides out GC pauses); drum hits skip it.
            Mixer.UseDirectVoices(sdl.CreateDirectVoice);
            sdl.Resume();
            // Its own high-priority thread: on the thread pool, scene loads and texture decoding
            // (pool work) delayed it past the ~11 ms queue, and the music stuttered.
            _producer = new Thread(() => produce(sdl)) { IsBackground = true, Name = "Audio mix", Priority = ThreadPriority.Highest };
            _producer.Start();
        }
        else if (OperatingSystem.IsWindows() && device is WasapiExclusiveOutput exclusive)
            exclusive.Start(render);
        else
            throw new ArgumentException($"Unsupported audio output {device.GetType().Name}.", nameof(device));
    }

    public AudioMixer Mixer { get; }

    private Exception? _failure;

    public Exception? Failure => _failure
        ?? (OperatingSystem.IsWindows() && _device is WasapiExclusiveOutput exclusive ? exclusive.Failure : null);

    public void SetPerformanceMonitoring(bool enabled) => _performanceMonitoring = enabled;

    public AudioPerformanceCounters GetPerformanceCounters() =>
        new(Interlocked.Read(ref _performanceBlocks), Interlocked.Read(ref _performanceWorkTicks));

    public AudioStreamTransport PlayTransport(ScheduledAudioSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_outputGate)
        {
            var transport = new AudioStreamTransport(source, () => _device.SubmittedFrames);
            transport.Handle = Mixer.PlayStream(transport, AudioBus.Bgm);
            return transport;
        }
    }

    /// <summary>Monotonic output-position estimate interpolated between hardware buffer updates.</summary>
    public TimeSpan GetPosition(AudioStreamTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_outputGate)
            return transport.Position(_device.SubmittedFrames, _device.QueuedFrames,
                TimeSpan.FromSeconds((double)_device.HardwareBufferFrames / _device.HardwareFormat.SampleRate));
    }

    /// <summary>
    /// Holds the song stream for a gameplay pause; other sounds (the pause menu's) keep playing. The
    /// device frames that pass meanwhile are left out of the song's position.
    /// </summary>
    public void SetPaused(AudioStreamTransport transport, bool paused)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_outputGate)
        {
            if (paused)
                _ = transport.Position(_device.SubmittedFrames, _device.QueuedFrames,
                    TimeSpan.FromSeconds((double)_device.HardwareBufferFrames / _device.HardwareFormat.SampleRate));
            transport.SetPaused(paused, _device.SubmittedFrames);
            Mixer.SetStreamPaused(transport.Handle, paused);
        }
    }

    public AudioPlaybackHandle PlayOneShot(
        string path,
        AudioBus bus = AudioBus.MenuSound,
        float volume = 1f,
        TimeSpan? maximumDuration = null,
        uint sourceStreamIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clip = AudioClip.Load(path, _device.Format, maximumDuration, sourceStreamIndex);
        return Mixer.Play(clip, bus, volume);
    }

    public AudioPlaybackHandle PlayLoop(
        string path,
        AudioBus bus,
        float volume = 1f,
        TimeSpan? maximumDuration = null,
        uint sourceStreamIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clip = AudioClip.Load(path, _device.Format, maximumDuration, sourceStreamIndex);
        return Mixer.Play(clip, bus, volume, loop: true);
    }

    private void render(Span<float> samples)
    {
        lock (_outputGate)
        {
            var monitoring = _performanceMonitoring;
            var start = monitoring ? Stopwatch.GetTimestamp() : 0;
            Mixer.Render(samples);
            if (monitoring)
            {
                Interlocked.Add(ref _performanceWorkTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Increment(ref _performanceBlocks);
            }
        }
    }

    private void produce(SdlAudioDevice device)
    {
        var samples = new float[RenderFrames * device.Format.Channels];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (device.QueuedFrames >= TargetQueuedFrames)
                {
                    Thread.Sleep(1);
                    continue;
                }
                lock (_outputGate)
                {
                    render(samples);
                    device.Queue(samples);
                }
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        try
        {
            _producer?.Join();
        }
        finally
        {
            Mixer.StopAll();
            if (_device is SdlAudioDevice sdl)
            {
                sdl.Pause();
                sdl.Clear();
            }
            _cancellation.Dispose();
        }
    }
}
