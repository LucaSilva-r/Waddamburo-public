using Waddamburo.Game.Patching;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Draws the built-in folders' art (our own, no game art): a faint pattern on the spine under its
/// name (56x400, over the folder's colour) and the open folder's picture (192x360): an emblem, the
/// headline and a few lines (counts).
/// </summary>
internal static class FolderArtPainter
{
    private const int BoxWidth = 192, BoxHeight = 360;
    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    private static readonly (byte R, byte G, byte B)[] Rainbow =
        [(235, 60, 60), (255, 150, 30), (250, 215, 40), (90, 200, 70), (50, 150, 240), (150, 90, 230)];

    /// <summary>
    /// The folder front's pattern (the open front's size, revealed through a mask as it opens): motifs
    /// over a transparent ground, drawn over the whole front, bevels included.
    /// </summary>
    public static RgbaTextSurface Pattern(FolderArt art, string fontPath, int scale)
    {
        var (width, height) = SongSelectGenrePatch.PatternSize;
        var canvas = new VectorCanvas(width, height, scale, fontPath);
        Paint(canvas, (x, y) => art.Style switch
        {
            // osu!: triangle outlines, as in its logo.
            FolderArtStyle.Osu => ((255, 255, 255), triangles(x, y) * 5),
            // Rainbow diagonal stripes.
            FolderArtStyle.Nijiiro => (Rainbow[(int)MathF.Floor((y + x) / 20) % Rainbow.Length], 1),
            // Don and ka notes.
            FolderArtStyle.Tja => note(x, y),
            // Dots on a staggered grid.
            _ => ((255, 255, 255), disc(x - (MathF.Floor(x / 14) * 14 + 7),
                y - (MathF.Floor(y / 14) * 14 + 7) - ((int)MathF.Floor(x / 14) % 2) * 7, 2.5f)),
        });
        return new RgbaTextSurface((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    public static RgbaTextSurface Box(FolderArt art, string fontPath, int scale)
    {
        var canvas = new VectorCanvas(BoxWidth, BoxHeight, scale, fontPath);
        const float cx = BoxWidth / 2f, cy = BoxHeight / 2f;
        switch (art.Style)
        {
            case FolderArtStyle.Osu:
                canvas.Image(OsuLogo.Value, null, cx, cy, 184, 184);
                break;
            case FolderArtStyle.Nijiiro:
                // A rainbow arch over the title.
                Paint(canvas, (x, y) =>
                {
                    var distance = MathF.Sqrt((x - cx) * (x - cx) + (y - cy - 20) * (y - cy - 20));
                    var band = (int)((92 - distance) / 12);
                    return y > cy + 20 || band < 0 || band >= Rainbow.Length ? (White, 0) : (Rainbow[band], Math.Clamp(92 - distance - band * 12, 0, 1));
                });
                canvas.Text("NIJIIRO", cx, cy + 50, BoxWidth - 12, 44, 0x000000);
                break;
            case FolderArtStyle.Tja:
                // A big don: red face in a white ring, black outline.
                Paint(canvas, (x, y) => ((20, 20, 20), disc(x - cx, y - cy, 88)));
                Paint(canvas, (x, y) => (White, disc(x - cx, y - cy, 82)));
                Paint(canvas, (x, y) => ((235, 69, 44), disc(x - cx, y - cy, 66)));
                break;
            default:
                // A magnifier: ring and handle.
                Paint(canvas, (x, y) => ((20, 20, 20), ring(x, y, cx - 16, cy - 16, 52, 22)));
                Paint(canvas, (x, y) => ((20, 20, 20), capsule(x, y, (cx + 24, cy + 24), (cx + 72, cy + 72), 15)));
                Paint(canvas, (x, y) => (White, ring(x, y, cx - 16, cy - 16, 52, 14)));
                Paint(canvas, (x, y) => (White, capsule(x, y, (cx + 26, cy + 26), (cx + 70, cy + 70), 11)));
                break;
        }
        return new RgbaTextSurface((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    private const int DescriptionWidth = 160, DescriptionHeight = 400, DescriptionColumns = 3;

    /// <summary>The genre description slot (160x400): up to three vertical columns, right to left.</summary>
    public static RgbaTextSurface Description(string lines, string fontPath, uint outlineRgb, int scale, bool squash)
    {
        var canvas = new VectorCanvas(DescriptionWidth, DescriptionHeight, scale, fontPath);
        const float column = DescriptionWidth / (float)DescriptionColumns;
        foreach (var (line, index) in lines.Split('\n').Where(static line => line.Length > 0).Take(DescriptionColumns)
            .Select(static (line, index) => (line, index)))
        {
            var text = NativeVerticalTextRasterizer.RenderSongTitle(fontPath, line, null, SongTitleTextProfile.Compact,
                outlineRgb, (uint)scale, squash);
            canvas.Image(text, null, DescriptionWidth - (index + 0.5f) * column, DescriptionHeight / 2f, column, DescriptionHeight,
                upscale: false);
        }
        return new RgbaTextSurface((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    private static readonly Lazy<RgbaTextSurface> OsuLogo = new(static () =>
    {
        using var stream = typeof(FolderArtPainter).Assembly.GetManifestResourceStream("Waddamburo.Resources.osu-logo.png")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var (width, height, rgba) = Waddamburo.Upscale.PngFile.Decode(memory.ToArray(), "osu-logo.png");
        return new RgbaTextSurface(width, height, rgba);
    });

    // Upward triangle outlines of a few sizes, scattered by a hash of their grid cell, faint.
    private static float triangles(float x, float y)
    {
        const float Cell = 28;
        float cx = MathF.Floor(x / Cell), cy = MathF.Floor(y / Cell);
        var hash = (uint)((int)cx * 73856093 ^ (int)cy * 19349663) * 2654435761u;
        if (hash % 3 == 0)
            return 0;
        var size = 14 + hash % 10;
        float ox = (cx + 0.5f) * Cell, oy = (cy + 0.5f) * Cell, h = size * 0.866f;
        // Inside an upward triangle centred at (ox, oy): above the base, under both sides.
        float dx = MathF.Abs(x - ox), dy = y - oy;
        var distance = Math.Max(dy - h / 2, (dx * 0.866f - (h / 2 - dy) * 0.5f));
        // Outlines (the logo's triangles are drawn as thin edges).
        return (0.18f + hash % 4 * 0.05f) * VectorCanvas.Coverage(MathF.Abs(distance) - 0.75f);
    }

    // Every pixel of the canvas through a pattern (stage units, pixel centres).
    private static void Paint(VectorCanvas canvas, Func<float, float, ((byte R, byte G, byte B) Colour, float Alpha)> pattern)
    {
        float s = canvas.Scale;
        for (var py = 0; py < canvas.PixelHeight; py++)
            for (var px = 0; px < canvas.PixelWidth; px++)
            {
                var (colour, alpha) = pattern((px + 0.5f) / s, (py + 0.5f) / s);
                canvas.Blend(px, py, colour, Math.Clamp(alpha, 0, 1));
            }
    }

    // Coverages (about a stage pixel of anti-aliasing).
    private static float disc(float dx, float dy, float radius) => VectorCanvas.Coverage(MathF.Sqrt(dx * dx + dy * dy) - radius);

    private static float ring(float x, float y, float cx, float cy, float radius, float thickness) =>
        VectorCanvas.Coverage(MathF.Abs(MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius) - thickness / 2);

    private static float capsule(float x, float y, (float X, float Y) a, (float X, float Y) b, float radius)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return disc(x - (a.X + t * dx), y - (a.Y + t * dy), radius);
    }

    // TJA: rows of small notes, don (red) and ka (blue) in turn, faint.
    private static ((byte R, byte G, byte B), float) note(float x, float y)
    {
        const float Cell = 26;
        float row = MathF.Floor(y / Cell), column = MathF.Floor((x + (row % 2) * Cell / 2) / Cell);
        float dx = x + (row % 2) * Cell / 2 - (column * Cell + Cell / 2), dy = y - (row * Cell + Cell / 2);
        var don = ((int)(row + column) & 1) == 0;
        return (don ? ((byte)235, (byte)69, (byte)44) : ((byte)68, (byte)194, (byte)220), disc(dx, dy, 8));
    }
}
