namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Feeds mixed PCM to one SDL device from a bounded background producer.</summary>
public sealed class AudioEngine : IDisposable
{
    private const int RenderFrames = 128;
    private const int TargetQueuedFrames = 512;

    private readonly SdlAudioDevice _device;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _producer;
    private readonly object _outputGate = new();
    private bool _disposed;

    public AudioEngine(SdlAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Mixer = new AudioMixer(device.Format);
        _device.Resume();
        _producer = Task.Run(produce);
    }

    public AudioMixer Mixer { get; }

    public Exception? Failure { get; private set; }

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

    private async Task produce()
    {
        var samples = new float[RenderFrames * _device.Format.Channels];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (_device.QueuedFrames >= TargetQueuedFrames)
                {
                    await Task.Delay(1, _cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                lock (_outputGate)
                {
                    Mixer.Render(samples);
                    _device.Queue(samples);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failure = exception;
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
            _producer.GetAwaiter().GetResult();
        }
        finally
        {
            Mixer.StopAll();
            _device.Pause();
            _device.Clear();
            _cancellation.Dispose();
        }
    }
}
