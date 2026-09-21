using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Media;

internal enum MediaResult
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    BackendUnavailable = 3,
    Io = 4,
    Unsupported = 5,
    Cancelled = 6,
    EndOfStream = 7,
    Internal = 8,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct MediaDecoderOptions
{
    internal uint StructSize;
    internal uint OutputSampleRate;
    internal uint OutputChannels;
    internal uint Flags;
    internal uint SourceStreamIndex;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct MediaStreamInfo
{
    internal uint StructSize;
    internal uint SampleRate;
    internal uint Channels;
    internal uint Reserved;
    internal ulong TotalFrames;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct MediaError
{
    internal uint StructSize;
    internal MediaResult Code;
    internal int NativeCode;
    internal uint MessageLength;
    internal fixed byte Message[256];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct MediaIoCallbacks
{
    internal uint StructSize;
    internal nint UserData;
    internal nint Read;
    internal nint Seek;
    internal nint ShouldCancel;
}

internal static partial class NativeMediaMethods
{
    internal const string LibraryName = "waddamburo_media";
    internal const uint AbiVersion = (1U << 16) | 1U;

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_negotiate_abi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult NegotiateAbi(uint requestedVersion, out uint negotiatedVersion);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_create_file")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult CreateFile(
        in MediaDecoderOptions options,
        nint utf8Path,
        out nint decoder,
        ref MediaError error);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_create_callbacks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult CreateCallbacks(
        in MediaDecoderOptions options,
        in MediaIoCallbacks callbacks,
        out nint decoder,
        ref MediaError error);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_get_stream_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult GetStreamInfo(nint decoder, ref MediaStreamInfo streamInfo);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_read_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult ReadFrames(
        nint decoder,
        nint interleavedSamples,
        ulong frameCapacity,
        out ulong framesRead);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_seek")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult Seek(nint decoder, ulong frameIndex);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_cancel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult Cancel(nint decoder);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_get_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MediaResult GetError(nint decoder, ref MediaError error);

    [LibraryImport(LibraryName, EntryPoint = "waddamburo_media_decoder_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint decoder);
}
