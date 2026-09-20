using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Text;

public sealed record RgbaTextSurface(uint Width, uint Height, byte[] Pixels);

public enum SongTitleTextProfile : uint
{
    Compact = 0,
    Expanded = 1,
}

/// <summary>Rasterizes a user-supplied font through Waddamburo's stable FreeType C adapter.</summary>
public static unsafe partial class NativeVerticalTextRasterizer
{
    private const uint AbiVersion = 0x0001_0001;

    public static RgbaTextSurface RenderSongTitle(
        string fontPath,
        string title,
        string? subtitle,
        SongTitleTextProfile profile,
        uint outlineRgb,
        uint rasterScale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfZero(rasterScale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rasterScale, 4U);
        if (!File.Exists(fontPath))
            throw new FileNotFoundException("The configured title font does not exist.", fontPath);
        if (GetAbiVersion() != AbiVersion)
            throw new InvalidOperationException("The native text rasterizer ABI is incompatible.");

        var baseDimensions = profile switch
        {
            SongTitleTextProfile.Compact => (Width: 56U, Height: 400U),
            SongTitleTextProfile.Expanded => (Width: 96U, Height: 400U),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        var width = checked(baseDimensions.Width * rasterScale);
        var height = checked(baseDimensions.Height * rasterScale);
        var pixels = new byte[checked((int)((ulong)width * height * 4))];
        var error = new NativeTextError { StructSize = (uint)Unsafe.SizeOf<NativeTextError>() };
        fixed (byte* destination = pixels)
        {
            var result = RenderSongTitle(
                fontPath,
                title,
                subtitle,
                profile,
                outlineRgb & 0x00ff_ffffU,
                rasterScale,
                width,
                height,
                destination,
                (ulong)pixels.Length,
                &error);
            if (result != 0)
                throw new InvalidOperationException(error.Message.Length == 0
                    ? $"Song title rasterization failed with code {result}."
                    : error.Message);
        }
        return new RgbaTextSurface(width, height, pixels);
    }

    public static RgbaTextSurface Render(string fontPath, string text, uint width, uint height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (!File.Exists(fontPath))
            throw new FileNotFoundException("The configured title font does not exist.", fontPath);
        if (GetAbiVersion() != AbiVersion)
            throw new InvalidOperationException("The native text rasterizer ABI is incompatible.");

        var pixels = new byte[checked((int)((ulong)width * height * 4))];
        var error = new NativeTextError { StructSize = (uint)Unsafe.SizeOf<NativeTextError>() };
        fixed (byte* destination = pixels)
        {
            var result = RenderVertical(fontPath, text, width, height, destination, (ulong)pixels.Length, &error);
            if (result != 0)
                throw new InvalidOperationException(error.Message.Length == 0
                    ? $"Title rasterization failed with code {result}."
                    : error.Message);
        }
        return new RgbaTextSurface(width, height, pixels);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private unsafe struct NativeTextError
    {
        public uint StructSize;
        public int Code;
        public int NativeCode;
        public uint MessageLength;
        public fixed byte MessageBytes[256];

        public string Message
        {
            get
            {
                fixed (byte* bytes = MessageBytes)
                    return Marshal.PtrToStringUTF8((nint)bytes, checked((int)Math.Min(MessageLength, 255))) ?? string.Empty;
            }
        }
    }

    [LibraryImport("waddamburo_text", EntryPoint = "waddamburo_text_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetAbiVersion();

    [LibraryImport(
        "waddamburo_text",
        EntryPoint = "waddamburo_text_render_vertical_rgba8",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RenderVertical(
        string fontPath,
        string text,
        uint width,
        uint height,
        byte* rgba8,
        ulong rgba8Capacity,
        NativeTextError* error);

    [LibraryImport(
        "waddamburo_text",
        EntryPoint = "waddamburo_text_render_song_title_rgba8",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RenderSongTitle(
        string fontPath,
        string title,
        string? subtitle,
        SongTitleTextProfile profile,
        uint outlineRgb,
        uint rasterScale,
        uint width,
        uint height,
        byte* rgba8,
        ulong rgba8Capacity,
        NativeTextError* error);
}
