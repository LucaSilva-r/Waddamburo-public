using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Sequential RGBA8 frames of a movie file (PAMF/MPEG program stream with H.264).</summary>
public sealed unsafe class NativeVideoDecoder : IDisposable
{
    private nint _handle;

    public NativeVideoDecoder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (NativeMediaMethods.NegotiateAbi(NativeMediaMethods.AbiVersion, out var negotiated)
            != MediaResult.Ok || negotiated != NativeMediaMethods.AbiVersion)
            throw new NotSupportedException("The native media library has an incompatible ABI.");
        var info = new MediaVideoInfo { StructSize = (uint)sizeof(MediaVideoInfo) };
        var error = new MediaError { StructSize = (uint)sizeof(MediaError) };
        var utf8Path = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var result = NativeMediaMethods.VideoOpenFile(utf8Path, out _handle, ref info, ref error);
            if (result != MediaResult.Ok)
                throw NativeAudioDecoder.createException(result, error);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Path);
        }
        Width = info.Width;
        Height = info.Height;
        Duration = info.DurationMicroseconds < 0 ? null : TimeSpan.FromMicroseconds(info.DurationMicroseconds);
    }

    public uint Width { get; }

    public uint Height { get; }

    public TimeSpan? Duration { get; }

    /// <summary>Decodes the next frame (Width * Height * 4 bytes); false after the last one.</summary>
    public bool TryReadFrame(Span<byte> rgba, out TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (byte* destination = rgba)
        {
            var result = NativeMediaMethods.VideoReadFrame(_handle, (nint)destination, (ulong)rgba.Length, out var pts);
            timestamp = TimeSpan.FromMicroseconds(pts);
            return result switch
            {
                MediaResult.Ok => true,
                MediaResult.EndOfStream => false,
                _ => throw new IOException($"Video decoding failed ({result})."),
            };
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
            NativeMediaMethods.VideoDestroy(_handle);
        _handle = 0;
    }
}
