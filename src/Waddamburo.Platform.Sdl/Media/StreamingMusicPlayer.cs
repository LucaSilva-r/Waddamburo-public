namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Incrementally decodes one song into a bounded SDL playback queue.</summary>
public sealed class StreamingMusicPlayer : IDisposable
{
    private const int DecodeFrames = 4096;

    private readonly SdlAudioDevice _device;
    private readonly NativeAudioDecoder _decoder;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _producer;
    private Exception? _failure;
    private bool _disposed;

    public StreamingMusicPlayer(SdlAudioDevice device, string path)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _decoder = new NativeAudioDecoder(
            path,
            checked((uint)device.Format.SampleRate),
            checked((uint)device.Format.Channels));
        _producer = Task.Run(produce);
    }

    public DecodedAudioInfo Info => _decoder.Info;

    public bool Playing { get; private set; }

    public Exception? Failure => _failure;

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.Resume();
        Playing = true;
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.Pause();
        Playing = false;
    }

    private async Task produce()
    {
        var samples = new float[DecodeFrames * _device.Format.Channels];
        var maximumQueuedFrames = _device.Format.SampleRate / 2;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (_device.QueuedFrames >= (ulong)maximumQueuedFrames)
                {
                    await Task.Delay(5, _cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                var count = _decoder.Read(samples);
                if (count == 0)
                {
                    _device.Flush();
                    break;
                }
                _device.Queue(samples.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
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
        _decoder.Cancel();
        try
        {
            _producer.GetAwaiter().GetResult();
        }
        finally
        {
            _decoder.Dispose();
            _cancellation.Dispose();
        }
    }
}
