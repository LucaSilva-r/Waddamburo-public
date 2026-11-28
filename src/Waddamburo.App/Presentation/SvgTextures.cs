using SkiaSharp;
using Svg.Skia;

namespace Waddamburo.App.Presentation;

/// <summary>
/// SVG textures of movie packages (docs/development/free-movies.md): drawn once, when the package loads,
/// at the document's own size times the package's texture scale.
/// </summary>
internal static class SvgTextures
{
    private const int MaxSide = 8192;

    /// <summary>
    /// The drawing at <paramref name="width"/> x <paramref name="height"/> pixels (the size a shape shows it at); a drawing
    /// of one flat colour (a background, a bar) stays at its own size, as it looks the same at any.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba)? DecodeAt(byte[] svg, int width, int height)
    {
        if (Decode(svg) is not { } own)
            return null;
        if (own.Width == width && own.Height == height || flat(own.Rgba))
            return own;
        using var document = new SKSvg();
        using var stream = new MemoryStream(svg);
        if (document.Load(stream) is not { } picture)
            return own;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / picture.CullRect.Width, height / picture.CullRect.Height);
            canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
            canvas.DrawPicture(picture);
        }
        return (width, height, bitmap.Bytes);
    }

    private static bool flat(byte[] rgba)
    {
        var first = BitConverter.ToUInt32(rgba, 0);
        for (var index = 4; index < rgba.Length; index += 4)
            if (BitConverter.ToUInt32(rgba, index) != first)
                return false;
        return true;
    }

    /// <summary>The drawing as straight RGBA rows; null when the bytes are not an SVG with a size.</summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(byte[] svg, float scale = 1)
    {
        using var document = new SKSvg();
        using var stream = new MemoryStream(svg);
        if (document.Load(stream) is not { } picture || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            return null;
        var width = (int)MathF.Ceiling(picture.CullRect.Width * scale);
        var height = (int)MathF.Ceiling(picture.CullRect.Height * scale);
        if (width > MaxSide || height > MaxSide)
            throw new InvalidDataException($"An SVG texture of {width} x {height} is too large ({MaxSide} at most).");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
            canvas.DrawPicture(picture);
        }
        return (width, height, bitmap.Bytes);
    }
}
