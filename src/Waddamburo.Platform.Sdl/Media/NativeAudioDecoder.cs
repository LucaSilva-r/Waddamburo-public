using System.Runtime.InteropServices;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

public readonly record struct DecodedAudioInfo(uint SampleRate, uint Channels, ulong? TotalFrames);

public sealed unsafe class NativeAudioDecoder : IDisposable
{
    private nint _handle;
    private readonly uint _channels;

    public NativeAudioDecoder(string path, uint outputSampleRate = 0, uint outputChannels = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (NativeMediaMethods.NegotiateAbi(NativeMediaMethods.AbiVersion, out var negotiated)
            != MediaResult.Ok || negotiated != NativeMediaMethods.AbiVersion)
        {
            throw new NotSupportedException("The native media library has an incompatible ABI.");
        }

        var options = new MediaDecoderOptions
        {
            StructSize = (uint)sizeof(MediaDecoderOptions),
            OutputSampleRate = outputSampleRate,
            OutputChannels = outputChannels,
        };
        var error = new MediaError { StructSize = (uint)sizeof(MediaError) };
        var utf8Path = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var result = NativeMediaMethods.CreateFile(in options, utf8Path, out _handle, ref error);
            if (result != MediaResult.Ok)
                throw createException(result, error);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Path);
        }

        try
        {
            var streamInfo = new MediaStreamInfo { StructSize = (uint)sizeof(MediaStreamInfo) };
            check(NativeMediaMethods.GetStreamInfo(_handle, ref streamInfo));
            _channels = streamInfo.Channels;
            Info = new DecodedAudioInfo(
                streamInfo.SampleRate,
                streamInfo.Channels,
                streamInfo.TotalFrames == ulong.MaxValue ? null : streamInfo.TotalFrames);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DecodedAudioInfo Info { get; }

    public int Read(Span<float> interleavedSamples)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (interleavedSamples.IsEmpty || interleavedSamples.Length % _channels != 0)
            throw new ArgumentException("The destination must hold a whole number of interleaved frames.", nameof(interleavedSamples));
        fixed (float* destination = interleavedSamples)
        {
            var result = NativeMediaMethods.ReadFrames(
                _handle,
                (nint)destination,
                (ulong)interleavedSamples.Length / _channels,
                out var framesRead);
            if (result == MediaResult.EndOfStream)
                return 0;
            check(result);
            return checked((int)(framesRead * _channels));
        }
    }

    public void Seek(ulong frameIndex)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        check(NativeMediaMethods.Seek(_handle, frameIndex));
    }

    public void Cancel()
    {
        if (_handle != 0)
            check(NativeMediaMethods.Cancel(_handle));
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;
        NativeMediaMethods.Destroy(_handle);
        _handle = 0;
    }

    private void check(MediaResult result)
    {
        if (result == MediaResult.Ok)
            return;
        var error = new MediaError { StructSize = (uint)sizeof(MediaError) };
        if (NativeMediaMethods.GetError(_handle, ref error) != MediaResult.Ok)
            throw new InvalidOperationException($"Native audio operation failed ({result}).");
        throw createException(result, error);
    }

    private static Exception createException(MediaResult result, MediaError error)
    {
        var length = Math.Min(error.MessageLength, 255U);
        string message;
        byte* bytes = error.Message;
        message = Encoding.UTF8.GetString(bytes, checked((int)length));
        if (string.IsNullOrWhiteSpace(message))
            message = $"Native audio operation failed ({result}).";
        return result switch
        {
            MediaResult.Io => new IOException(message),
            MediaResult.Unsupported or MediaResult.BackendUnavailable or MediaResult.AbiMismatch
                => new NotSupportedException(message),
            MediaResult.Cancelled => new OperationCanceledException(message),
            MediaResult.InvalidArgument => new ArgumentException(message),
            _ => new InvalidOperationException(message),
        };
    }
}
