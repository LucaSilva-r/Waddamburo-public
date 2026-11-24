using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>RGBA8 frames of a movie file (PAMF, or a background video: MP4, AVI, FLV, MKV, WMV...), at most 720 lines high.</summary>
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

    public uint Width { get; private set; }

    public uint Height { get; private set; }

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

    /// <summary>The next <see cref="TryReadFrame"/> returns the first frame at or after <paramref name="time"/>.</summary>
    public void Seek(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var result = NativeMediaMethods.VideoSeek(_handle, (long)time.TotalMicroseconds);
        if (result != MediaResult.Ok)
            throw new IOException($"Video seeking failed ({result}).");
    }

    /// <summary>Frames come out at most <paramref name="height"/> lines high (the shape kept); before the first frame.</summary>
    public void LimitHeight(uint height)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (NativeMediaMethods.VideoLimitHeight(_handle, height, out var width, out var limited) == MediaResult.Ok)
            (Width, Height) = (width, limited);
    }

    /// <summary>True: B-frames (nothing refers to them) are not decoded, for a decoder running late.</summary>
    public bool SkipUnreferenced
    {
        set
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            NativeMediaMethods.VideoSkipUnreferenced(_handle, value ? 1 : 0);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
            NativeMediaMethods.VideoDestroy(_handle);
        _handle = 0;
    }
}
