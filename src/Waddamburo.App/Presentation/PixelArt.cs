namespace Waddamburo.App.Presentation;

/// <summary>
/// Pixel art in the game's data, which nothing in the files marks: NTP3 headers carry no filter field and
/// every Lumen bitmap fill is Flash's smoothed kind (0x40/0x41, never 0x42/0x43), so it is listed here by
/// hand (checked in the viewer). It is never AI-upscaled; it is enlarged by whole pixels instead (nearest
/// neighbour), and the renderer's bilinear filter only smooths what is left between pixels ("sharp bilinear").
/// </summary>
internal static class PixelArt
{
    // The Super Mario Bros. gameplay skin: all of it.
    private static readonly string[] Archives = ["enso_mario"];

    // Enlarged at most this far, and to at most this many pixels on the long side (uploaded as RGBA): a
    // screen-wide 1280 strip gets x3, a 4K screen's own scale.
    private const int MaxFactor = 4, MaxSide = 4096;

    /// <summary>Whether a Lumen archive (e.g. "enso_mario/packeddata.ddp") is pixel art.</summary>
    public static bool IsArchive(string archiveId) =>
        Archives.Any(name => archiveId.Replace('\\', '/').Split('/')[0].Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pixels enlarged by a whole factor (nearest neighbour); the same array when no factor above 1 fits.</summary>
    public static (int Width, int Height, byte[] Rgba) Enlarge(int width, int height, byte[] rgba)
    {
        var factor = Math.Min(MaxFactor, MaxSide / Math.Max(1, Math.Max(width, height)));
        if (factor < 2)
            return (width, height, rgba);
        var (outWidth, outHeight) = (width * factor, height * factor);
        var output = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var source = (y / factor) * width * 4;
            var target = y * outWidth * 4;
            for (var x = 0; x < outWidth; x++)
                rgba.AsSpan(source + x / factor * 4, 4).CopyTo(output.AsSpan(target + x * 4));
        }
        return (outWidth, outHeight, output);
    }
}
