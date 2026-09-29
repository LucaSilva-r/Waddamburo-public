using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Text;

public sealed record RgbaTextSurface(uint Width, uint Height, byte[] Pixels);

/// <summary>
/// One rendered glyph: its fill and outline masks (8-bit, <see cref="Width"/> x <see cref="Height"/>), the
/// masks' top-left from the pen position on the baseline (<see cref="Top"/> above it), and the size's metrics.
/// </summary>
public sealed record GlyphMasks(int Width, int Height, int Left, int Top, float Advance, float Ascender, float Descender,
    byte[] Fill, byte[] Outline);

public enum SongTitleTextProfile : uint
{
    Compact = 0,
    Expanded = 1,
    Transition = 2,
    /// <summary>Gameplay song_info title: fixed height, right-aligned, squeezed to fit.</summary>
    GameplayTitle = 3,
}

/// <summary>Rasterizes a user-supplied font through Waddamburo's stable FreeType C adapter.</summary>
public static unsafe partial class NativeVerticalTextRasterizer
{
    private const uint AbiVersion = 0x0001_0006;
    private const uint SquashFlag = 1; // WADDAMBURO_TEXT_SONG_TITLE_SQUASH
    private static readonly ConcurrentDictionary<string, Lazy<NativeFontContext>> FontContexts =
        new(StringComparer.Ordinal);

    public static RgbaTextSurface RenderSongTitle(
        string fontPath,
        string title,
        string? subtitle,
        SongTitleTextProfile profile,
        uint outlineRgb,
        uint rasterScale,
        bool squash = false)
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
            SongTitleTextProfile.Transition => (Width: 720U, Height: 103U),
            SongTitleTextProfile.GameplayTitle => (Width: 720U, Height: 64U),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        var width = checked(baseDimensions.Width * rasterScale);
        var height = checked(baseDimensions.Height * rasterScale);
        var fullFontPath = Path.GetFullPath(fontPath);
        var context = FontContexts.GetOrAdd(
            fullFontPath,
            static path => new Lazy<NativeFontContext>(
                () => new NativeFontContext(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return context.Render(title, subtitle, profile, squash ? SquashFlag : 0, outlineRgb, rasterScale, width, height);
    }

    /// <summary>One character at <paramref name="fontPx"/> with its <paramref name="outlineRadius"/> border.</summary>
    public static GlyphMasks RenderGlyph(string fontPath, int scalar, float fontPx, uint outlineRadius)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        if (!File.Exists(fontPath))
            throw new FileNotFoundException("The configured title font does not exist.", fontPath);
        if (GetAbiVersion() != AbiVersion)
            throw new InvalidOperationException("The native text rasterizer ABI is incompatible.");
        return FontContexts.GetOrAdd(
            Path.GetFullPath(fontPath),
            static path => new Lazy<NativeFontContext>(
                () => new NativeFontContext(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value.RenderGlyph(scalar, fontPx, outlineRadius);
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

    private sealed class NativeFontContext : IDisposable
    {
        private readonly NativeTextContextHandle _handle;
        private readonly object _sync = new();

        public NativeFontContext(string fontPath)
        {
            var error = new NativeTextError { StructSize = (uint)Unsafe.SizeOf<NativeTextError>() };
            var handle = CreateContext(fontPath, &error);
            if (handle == 0)
                throw new InvalidOperationException(error.Message.Length == 0
                    ? "FreeType could not create a font context."
                    : error.Message);
            _handle = new NativeTextContextHandle(handle);
        }

        public RgbaTextSurface Render(
            string title,
            string? subtitle,
            SongTitleTextProfile profile,
            uint flags,
            uint outlineRgb,
            uint rasterScale,
            uint width,
            uint height)
        {
            var pixels = new byte[checked((int)((ulong)width * height * 4))];
            var error = new NativeTextError { StructSize = (uint)Unsafe.SizeOf<NativeTextError>() };
            lock (_sync)
            {
                fixed (byte* destination = pixels)
                {
                    var result = RenderSongTitleWithContext(
                        _handle.DangerousGetHandle(),
                        title,
                        subtitle,
                        profile,
                        flags,
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
            }
            return new RgbaTextSurface(width, height, pixels);
        }

        public GlyphMasks RenderGlyph(int scalar, float fontPx, uint outlineRadius)
        {
            var error = new NativeTextError { StructSize = (uint)Unsafe.SizeOf<NativeTextError>() };
            var glyph = new NativeTextGlyph { StructSize = (uint)Unsafe.SizeOf<NativeTextGlyph>() };
            lock (_sync)
            {
                // First the size, then the masks.
                check(RenderGlyphWithContext(_handle.DangerousGetHandle(), (uint)scalar, fontPx, outlineRadius, &glyph,
                    null, null, 0, &error), error);
                var fill = new byte[checked((int)(glyph.Width * glyph.Height))];
                var outline = new byte[fill.Length];
                fixed (byte* fillMask = fill)
                fixed (byte* outlineMask = outline)
                    check(RenderGlyphWithContext(_handle.DangerousGetHandle(), (uint)scalar, fontPx, outlineRadius, &glyph,
                        fillMask, outlineMask, (ulong)fill.Length, &error), error);
                return new GlyphMasks((int)glyph.Width, (int)glyph.Height, glyph.Left, glyph.Top, glyph.Advance,
                    glyph.Ascender, glyph.Descender, fill, outline);
            }

            static void check(int result, NativeTextError error)
            {
                if (result != 0)
                    throw new InvalidOperationException(error.Message.Length == 0
                        ? $"Glyph rasterization failed with code {result}."
                        : error.Message);
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTextGlyph
    {
        public uint StructSize;
        public uint Width;
        public uint Height;
        public int Left;
        public int Top;
        public float Advance;
        public float Ascender;
        public float Descender;
    }

    private sealed class NativeTextContextHandle : SafeHandle
    {
        public NativeTextContextHandle(nint value) : base(nint.Zero, true) => SetHandle(value);

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            DestroyContext(handle);
            return true;
        }
    }

    [LibraryImport("waddamburo_text", EntryPoint = "waddamburo_text_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetAbiVersion();

    [LibraryImport(
        "waddamburo_text",
        EntryPoint = "waddamburo_text_context_create",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateContext(string fontPath, NativeTextError* error);

    [LibraryImport("waddamburo_text", EntryPoint = "waddamburo_text_context_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyContext(nint context);

    [LibraryImport(
        "waddamburo_text",
        EntryPoint = "waddamburo_text_context_render_song_title_ex_rgba8",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RenderSongTitleWithContext(
        nint context,
        string title,
        string? subtitle,
        SongTitleTextProfile profile,
        uint flags,
        uint outlineRgb,
        uint rasterScale,
        uint width,
        uint height,
        byte* rgba8,
        ulong rgba8Capacity,
        NativeTextError* error);

    [LibraryImport("waddamburo_text", EntryPoint = "waddamburo_text_context_render_glyph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RenderGlyphWithContext(
        nint context,
        uint scalar,
        float fontPx,
        uint outlineRadius,
        NativeTextGlyph* glyph,
        byte* fill,
        byte* outline,
        ulong maskCapacity,
        NativeTextError* error);

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

}
