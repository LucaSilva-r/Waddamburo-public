using System.Collections.Concurrent;
using System.Text;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The built-in folders' art: the pattern over the front and the emblem in the mascot slot are PNG files
/// built into the app (Resources/folders/&lt;style&gt;/{pattern,box}.png: edit them, rebuild); the description
/// columns are drawn.
/// </summary>
internal static class FolderArtPainter
{
    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    private static readonly ConcurrentDictionary<string, RgbaTextSurface> Images = new();

    /// <summary>The folder front's pattern (any size: drawn over the open front, revealed as it opens).</summary>
    public static RgbaTextSurface Pattern(FolderArt art) => image(art.Style, "pattern");

    /// <summary>The open folder's emblem (any size: drawn into the 192x360 mascot slot).</summary>
    public static RgbaTextSurface Box(FolderArt art) => image(art.Style, "box");

    private static RgbaTextSurface image(FolderArtStyle style, string part) =>
        Images.GetOrAdd($"{style.ToString().ToLowerInvariant()}.{part}", static name =>
        {
            // Default resource names join the folders with dots (…Resources.folders.osu.box.png).
            var assembly = typeof(FolderArtPainter).Assembly;
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(entry => entry.EndsWith($".folders.{name}.png", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Folder art {name}.png is not built in.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var (width, height, rgba) = Waddamburo.Upscale.PngFile.Decode(memory.ToArray(), name + ".png");
            return new RgbaTextSurface(width, height, rgba);
        });

    private const int DescriptionWidth = 160, DescriptionHeight = 400, DescriptionColumns = 3;

    /// <summary>
    /// The genre description slot (160x400): up to three vertical columns, right to left, each starting
    /// lower than the one before (the game's own descriptions are staggered so).
    /// </summary>
    public static RgbaTextSurface Description(string lines, string fontPath, uint outlineRgb, int scale, bool squash)
    {
        var canvas = new VectorCanvas(DescriptionWidth, DescriptionHeight, scale, fontPath);
        foreach (var (line, index) in lines.Split('\n').Where(static line => line.Length > 0).Take(DescriptionColumns)
            .Select(static (line, index) => (line, index)))
            column(canvas, fontPath, line, DescriptionWidth - 4 - (index + 0.5f) * ColumnPitch, ColumnTop + index * ColumnStagger,
                DescriptionHeight - 4, outlineRgb);
        return new RgbaTextSurface((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    // Description text (stage units): glyph size, outline radius, character step, column pitch, the first
    // column's top and how much lower each next column starts (as the game's own descriptions).
    private const float GlyphSize = 30, GlyphOutline = 4.5f, CharacterStep = 31, ColumnPitch = 40, ColumnTop = 6, ColumnStagger = 44;

    // One vertical line of characters drawn at its final size (white over a thick outline), centred on
    // `centreX` from `top`, squeezed vertically when it would pass `bottom`.
    private static void column(VectorCanvas canvas, string fontPath, string text, float centreX, float top, float bottom, uint outlineRgb)
    {
        var scale = canvas.Scale;
        var runes = text.EnumerateRunes().ToArray();
        var step = Math.Min(CharacterStep, (bottom - top - GlyphSize) / Math.Max(1, runes.Length - 1));
        (byte, byte, byte) outline = ((byte)(outlineRgb >> 16), (byte)(outlineRgb >> 8), (byte)outlineRgb);
        for (var index = 0; index < runes.Length; index++)
        {
            if (Rune.IsWhiteSpace(runes[index]))
                continue;
            var glyph = NativeVerticalTextRasterizer.RenderGlyph(fontPath, runes[index].Value, GlyphSize * scale,
                (uint)(GlyphOutline * scale + 0.5f));
            // The glyph's box centred in its cell (its ink, not its baseline: Latin letters sit evenly).
            var x0 = (int)(centreX * scale - glyph.Width / 2f);
            var y0 = (int)((top + index * step + GlyphSize / 2) * scale - glyph.Height / 2f);
            for (var pass = 0; pass < 2; pass++)
                for (var row = 0; row < glyph.Height; row++)
                    for (var columnIndex = 0; columnIndex < glyph.Width; columnIndex++)
                    {
                        var mask = (pass == 0 ? glyph.Outline : glyph.Fill)[row * glyph.Width + columnIndex];
                        if (mask != 0)
                            canvas.Blend(x0 + columnIndex, y0 + row, pass == 0 ? outline : White, mask / 255f);
                    }
        }
    }

}
