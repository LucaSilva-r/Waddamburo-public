using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

public readonly record struct DecodedAudioInfo(
    uint SampleRate,
    uint Channels,
    ulong? TotalFrames,
    ulong? LoopStartFrame,
    ulong? LoopEndFrame);

public sealed unsafe class NativeAudioDecoder : IDisposable
{
    private nint _handle;
    private readonly uint _channels;
    private GCHandle _inputHandle;
    private InputState? _inputState;

    public NativeAudioDecoder(
        string path,
        uint outputSampleRate = 0,
        uint outputChannels = 0,
        uint sourceStreamIndex = 0)
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
            SourceStreamIndex = sourceStreamIndex,
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
                optionalFrame(streamInfo.TotalFrames),
                optionalFrame(streamInfo.LoopStartFrame),
                optionalFrame(streamInfo.LoopEndFrame));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a decoder over a provider-owned stream. The decoder takes ownership of
    /// <paramref name="input"/> unless <paramref name="leaveOpen"/> is true.
    /// </summary>
    public NativeAudioDecoder(
        Stream input,
        uint outputSampleRate = 0,
        uint outputChannels = 0,
        uint sourceStreamIndex = 0,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        if (NativeMediaMethods.NegotiateAbi(NativeMediaMethods.AbiVersion, out var negotiated)
            != MediaResult.Ok || negotiated != NativeMediaMethods.AbiVersion)
        {
            throw new NotSupportedException("The native media library has an incompatible ABI.");
        }

        var state = new InputState(input, leaveOpen);
        _inputState = state;
        _inputHandle = GCHandle.Alloc(state);
        var options = new MediaDecoderOptions
        {
            StructSize = (uint)sizeof(MediaDecoderOptions),
            OutputSampleRate = outputSampleRate,
            OutputChannels = outputChannels,
            SourceStreamIndex = sourceStreamIndex,
        };
        var callbacks = new MediaIoCallbacks
        {
            StructSize = (uint)sizeof(MediaIoCallbacks),
            UserData = GCHandle.ToIntPtr(_inputHandle),
            Read = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, ulong, ulong*, MediaResult>)&read,
            Seek = input.CanSeek
                ? (nint)(delegate* unmanaged[Cdecl]<nint, long, uint, ulong*, MediaResult>)&seek
                : 0,
            ShouldCancel = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&shouldCancel,
        };
        var error = new MediaError { StructSize = (uint)sizeof(MediaError) };
        try
        {
            var result = NativeMediaMethods.CreateCallbacks(in options, in callbacks, out _handle, ref error);
            if (result != MediaResult.Ok)
                throw state.Failure ?? createException(result, error);

            var streamInfo = new MediaStreamInfo { StructSize = (uint)sizeof(MediaStreamInfo) };
            check(NativeMediaMethods.GetStreamInfo(_handle, ref streamInfo));
            _channels = streamInfo.Channels;
            Info = new DecodedAudioInfo(
                streamInfo.SampleRate,
                streamInfo.Channels,
                optionalFrame(streamInfo.TotalFrames),
                optionalFrame(streamInfo.LoopStartFrame),
                optionalFrame(streamInfo.LoopEndFrame));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DecodedAudioInfo Info { get; }

    private static ulong? optionalFrame(ulong value) => value == ulong.MaxValue ? null : value;

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
        if (_inputState is { } state)
            state.Cancelled = true;
        if (_handle != 0)
            check(NativeMediaMethods.Cancel(_handle));
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMediaMethods.Destroy(_handle);
            _handle = 0;
        }
        releaseInput();
    }

    private void releaseInput()
    {
        var state = _inputState;
        _inputState = null;
        if (_inputHandle.IsAllocated)
            _inputHandle.Free();
        if (state is { LeaveOpen: false })
            state.Stream.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static MediaResult read(nint userData, byte* destination, ulong capacity, ulong* bytesRead)
    {
        if (destination is null || bytesRead is null)
            return MediaResult.InvalidArgument;
        *bytesRead = 0;
        var state = stateFrom(userData);
        if (state is null)
            return MediaResult.InvalidArgument;
        if (state.Cancelled)
            return MediaResult.Cancelled;
        try
        {
            var count = state.Stream.Read(new Span<byte>(destination, checked((int)Math.Min(capacity, int.MaxValue))));
            *bytesRead = checked((ulong)count);
            return count == 0 ? MediaResult.EndOfStream : MediaResult.Ok;
        }
        catch (Exception exception)
        {
            state.Failure = exception;
            return MediaResult.Io;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static MediaResult seek(nint userData, long offset, uint origin, ulong* position)
    {
        if (position is null)
            return MediaResult.InvalidArgument;
        var state = stateFrom(userData);
        if (state is null)
            return MediaResult.InvalidArgument;
        if (state.Cancelled)
            return MediaResult.Cancelled;
        try
        {
            var seekOrigin = origin switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            var newPosition = state.Stream.Seek(offset, seekOrigin);
            if (newPosition < 0)
                return MediaResult.Io;
            *position = checked((ulong)newPosition);
            return MediaResult.Ok;
        }
        catch (Exception exception)
        {
            state.Failure = exception;
            return MediaResult.Io;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int shouldCancel(nint userData) => stateFrom(userData)?.Cancelled == true ? 1 : 0;

    private static InputState? stateFrom(nint userData) =>
        userData == 0 ? null : GCHandle.FromIntPtr(userData).Target as InputState;

    private void check(MediaResult result)
    {
        if (result == MediaResult.Ok)
            return;
        var error = new MediaError { StructSize = (uint)sizeof(MediaError) };
        if (NativeMediaMethods.GetError(_handle, ref error) != MediaResult.Ok)
            throw new InvalidOperationException($"Native audio operation failed ({result}).");
        throw createException(result, error);
    }

    internal static Exception createException(MediaResult result, MediaError error)
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

    private sealed class InputState(Stream stream, bool leaveOpen)
    {
        public Stream Stream { get; } = stream;

        public bool LeaveOpen { get; } = leaveOpen;

        public volatile bool Cancelled;

        public Exception? Failure;
    }
}
