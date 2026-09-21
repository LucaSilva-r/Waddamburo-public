namespace Waddamburo.Platform.Sdl.Media;

/// <summary>A non-blocking, device-format source consumed by <see cref="AudioMixer"/>.</summary>
public interface IAudioStreamSource : IDisposable
{
    SdlAudioFormat Format { get; }

    bool IsCompleted { get; }

    Exception? Failure { get; }

    int Read(Span<float> interleavedDestination);
}

/// <summary>Incrementally decodes a stream into a bounded PCM ring buffer.</summary>
public sealed class BufferedAudioSource : IAudioStreamSource
{
    private const int DecodeFrames = 4096;

    private readonly object _gate = new();
    private readonly NativeAudioDecoder _decoder;
    private readonly float[] _ring;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _producer;
    private int _readPosition;
    private int _sampleCount;
    private bool _endOfStream;
    private bool _disposed;
    private Exception? _failure;

    public BufferedAudioSource(Stream input, SdlAudioFormat format, TimeSpan start = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        Format = format;
        _decoder = new NativeAudioDecoder(
            input,
            checked((uint)format.SampleRate),
            checked((uint)format.Channels));
        if (start > TimeSpan.Zero)
        {
            var frame = checked((ulong)Math.Round(start.TotalSeconds * format.SampleRate));
            _decoder.Seek(frame);
        }
        _ring = new float[checked(format.SampleRate * format.Channels / 2)];
        _producer = Task.Run(produce);
    }

    public SdlAudioFormat Format { get; }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _endOfStream && _sampleCount == 0;
        }
    }

    public Exception? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    public int Read(Span<float> interleavedDestination)
    {
        if (interleavedDestination.Length % Format.Channels != 0)
            throw new ArgumentException("The destination must hold whole interleaved frames.", nameof(interleavedDestination));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var count = Math.Min(interleavedDestination.Length, _sampleCount);
            var first = Math.Min(count, _ring.Length - _readPosition);
            _ring.AsSpan(_readPosition, first).CopyTo(interleavedDestination);
            _ring.AsSpan(0, count - first).CopyTo(interleavedDestination[first..]);
            _readPosition = (_readPosition + count) % _ring.Length;
            _sampleCount -= count;
            Monitor.PulseAll(_gate);
            return count;
        }
    }

    private void produce()
    {
        var decoded = new float[DecodeFrames * Format.Channels];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var count = _decoder.Read(decoded);
                if (count == 0)
                    break;
                var offset = 0;
                while (offset < count)
                {
                    lock (_gate)
                    {
                        while (_sampleCount == _ring.Length && !_cancellation.IsCancellationRequested)
                            Monitor.Wait(_gate);
                        if (_cancellation.IsCancellationRequested)
                            return;
                        var writePosition = (_readPosition + _sampleCount) % _ring.Length;
                        var writable = Math.Min(count - offset, _ring.Length - _sampleCount);
                        var first = Math.Min(writable, _ring.Length - writePosition);
                        decoded.AsSpan(offset, first).CopyTo(_ring.AsSpan(writePosition));
                        decoded.AsSpan(offset + first, writable - first).CopyTo(_ring);
                        _sampleCount += writable;
                        offset += writable;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_gate)
                _failure = exception;
        }
        finally
        {
            lock (_gate)
            {
                _endOfStream = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cancellation.Cancel();
            Monitor.PulseAll(_gate);
        }
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
