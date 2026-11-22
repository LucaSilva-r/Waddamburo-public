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
    // ~21 ms at 48 kHz: rides out the GC's own pauses while scenes load (8-20 ms traced); drum hits skip it.
    private const int MinimumQueuedFrames = 1024;

    private IAudioOutput _device;
    private CancellationTokenSource _cancellation = new();
    private Thread? _producer;
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
        attach(device);
    }

    /// <summary>The output the mix plays on (the engine owns it after <see cref="SwitchOutput"/>).</summary>
    public IAudioOutput Output => _device;

    /// <summary>
    /// Plays the mix on another output: everything stops, the current output is closed first (exclusive and ASIO
    /// outputs hold the device), then <paramref name="open"/> opens the next. The mixer, its buses and every clip stay.
    /// False when the next output runs at another format than the mix (the clips were decoded for it): it is closed
    /// and the engine stays silent; the caller restarts.
    /// </summary>
    public bool SwitchOutput(Func<IAudioOutput> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellation.Cancel();
        _producer?.Join();
        _producer = null;
        Mixer.StopAll();
        Mixer.UseDirectVoices(null); // the old device destroys them
        // Not under _outputGate: a pull output's thread renders under it, and disposing joins that thread.
        _device.Dispose();
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        _failure = null;
        var next = open();
        _device = next;
        if (next.Format != Mixer.Format)
        {
            next.Dispose();
            return false;
        }
        attach(next);
        return true;
    }

    private void attach(IAudioOutput device)
    {
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
        else if (device is IPullAudioOutput pull)
            pull.Start(render);
        else
            throw new ArgumentException($"Unsupported audio output {device.GetType().Name}.", nameof(device));
    }

    public AudioMixer Mixer { get; }

    /// <summary>What the game is doing (scene, tick), named in the underrun trace; read on the mix thread.</summary>
    public Func<string>? TraceContext { get; set; }

    private volatile Exception? _failure;

    public Exception? Failure => _failure
        ?? (_device as IPullAudioOutput)?.Failure;

    public void SetPerformanceMonitoring(bool enabled) => _performanceMonitoring = enabled;

    public AudioPerformanceCounters GetPerformanceCounters() =>
        new(Interlocked.Read(ref _performanceBlocks), Interlocked.Read(ref _performanceWorkTicks));

    public AudioStreamTransport PlayTransport(ScheduledAudioSource source, AudioBus bus = AudioBus.Bgm, float volume = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_outputGate)
        {
            var transport = new AudioStreamTransport(source, () => _device.SubmittedFrames,
                () => $"{_device.Diagnostics}; {TraceContext?.Invoke()}");
            transport.Handle = Mixer.PlayStream(transport, bus, volume);
            return transport;
        }
    }

    /// <summary>Monotonic output-position estimate, held at its last reading if the device fails.</summary>
    public TimeSpan GetPosition(AudioStreamTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_outputGate)
            return Failure is not null ? transport.LastPosition
                : transport.Position(_device.SubmittedFrames, _device.QueuedFrames,
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
            if (Failure is not null)
                return;
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
        return playFile(path, bus, volume, maximumDuration, sourceStreamIndex, loop: false);
    }

    private readonly Dictionary<string, Task<AudioClip>> _preloaded = [];

    /// <summary>Decodes a file in the background and keeps it for <see cref="PlayLoop"/> and <see cref="PlayOneShot"/>.</summary>
    public void Preload(string path)
    {
        var format = _device.Format;
        _preloaded.TryAdd(Path.GetFullPath(path), Task.Run(() => AudioClip.Load(path, format)));
    }

    /// <summary>Plays a file decoded off this thread (a jingle took 0.7 s to decode on a Switch): silent until it is ready.</summary>
    private AudioPlaybackHandle playFile(string path, AudioBus bus, float volume, TimeSpan? maximumDuration, uint sourceStreamIndex, bool loop)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The audio file does not exist.", path);
        var format = _device.Format;
        var kept = maximumDuration is null && sourceStreamIndex == 0 ? _preloaded.GetValueOrDefault(Path.GetFullPath(path)) : null;
        var decoding = kept ?? Task.Run(() => AudioClip.Load(path, format, maximumDuration, sourceStreamIndex));
        _ = decoding.ContinueWith(task => Console.Error.WriteLine($"{Path.GetFileName(path)} is unavailable: {task.Exception!.InnerException!.Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return Mixer.PlayStream(new DecodingClipSource(format, decoding, loop), bus, volume);
    }

    public AudioPlaybackHandle PlayLoop(
        string path,
        AudioBus bus,
        float volume = 1f,
        TimeSpan? maximumDuration = null,
        uint sourceStreamIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return playFile(path, bus, volume, maximumDuration, sourceStreamIndex, loop: true);
    }

    /// <summary>
    /// Queues mixed audio up to <paramref name="ahead"/> now, so a known stall of every managed thread (a blocking
    /// collection) plays through: SDL's device thread is native and keeps draining it. Sounds started in that
    /// window wait behind it.
    /// </summary>
    public void FillAhead(TimeSpan ahead)
    {
        if (_device is not SdlAudioDevice device || _disposed)
            return;
        var target = (ulong)(ahead.TotalSeconds * device.Format.SampleRate);
        var samples = new float[RenderFrames * device.Format.Channels];
        lock (_outputGate)
            while (Failure is null && device.QueuedFrames < target)
            {
                render(samples);
                device.Queue(samples);
            }
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
        var rate = (double)device.Format.SampleRate;
        // At least two device periods: a device that pulls a whole period at once (Android) must find the next one
        // already queued.
        // ponytail: phones queue 2048 (~43 ms): Android's mixer pulls 20 ms periods (more than SDL reports) and the
        // mix thread oversleeps by a few ms there; use the real burst (AAudio framesPerBurst) once SDL exposes it.
        var target = (ulong)Math.Max(PlatformInfo.IsMobile ? 2048 : MinimumQueuedFrames, 2 * device.HardwareBufferFrames);
        // The underrun trace: when the mix last queued, how much it left queued, the GC's pause total then.
        long lastQueued = 0, lastWake = Stopwatch.GetTimestamp();
        ulong queuedAfter = 0;
        var gcPause = GC.GetTotalPauseDuration();
        var collections = GC.CollectionCount(2);
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (device.QueuedFrames >= target)
                {
                    lastWake = Stopwatch.GetTimestamp();
                    Thread.Sleep(1);
                    continue;
                }
                var waiting = Stopwatch.GetTimestamp();
                lock (_outputGate)
                {
                    var locked = Stopwatch.GetTimestamp();
                    render(samples);
                    var rendered = Stopwatch.GetTimestamp();
                    // The queue ran dry since the last block: the device played silence for the rest of the gap.
                    if (lastQueued != 0 && device.QueuedFrames == 0)
                    {
                        var starved = Stopwatch.GetElapsedTime(lastQueued, rendered).TotalMilliseconds - queuedAfter / rate * 1000;
                        if (starved >= 1)
                            traceUnderrun(starved, Stopwatch.GetElapsedTime(lastWake, waiting).TotalMilliseconds,
                                Stopwatch.GetElapsedTime(waiting, locked).TotalMilliseconds,
                                Stopwatch.GetElapsedTime(locked, rendered).TotalMilliseconds,
                                gcPause, collections, device);
                    }
                    device.Queue(samples);
                    lastQueued = Stopwatch.GetTimestamp();
                    queuedAfter = device.QueuedFrames;
                    gcPause = GC.GetTotalPauseDuration();
                    collections = GC.CollectionCount(2);
                }
                lastWake = lastQueued;
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    // One line per underrun (the log keeps it, with the scene around it): how long the device starved, and
    // where the mix thread lost the time: oversleeping (or descheduled), waiting for the mixer's lock (the
    // main thread holds it to start, stop or seek sounds), mixing, or a garbage collection's pause.
    private void traceUnderrun(double starved, double slept, double lockWait, double mix,
        TimeSpan gcPause, int collections, SdlAudioDevice device)
    {
        string context;
        try
        {
            context = TraceContext?.Invoke() ?? "";
        }
        catch (Exception exception)
        {
            context = exception.GetType().Name;
        }
        Console.WriteLine(FormattableString.Invariant(
            $"Audio underrun: {starved:F1} ms of silence (device buffer {device.HardwareBufferFrames * 1000.0 / device.HardwareFormat.SampleRate:F1} ms); ")
            + FormattableString.Invariant($"since last wake {slept:F1} ms, lock wait {lockWait:F1} ms, mix {mix:F1} ms, ")
            + FormattableString.Invariant($"GC pauses +{(GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds:F1} ms (gen2 +{GC.CollectionCount(2) - collections}); {context}"));
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
