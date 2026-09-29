using System.Collections.Concurrent;

namespace Waddamburo.Platform.Sdl.Text;

/// <summary>
/// One outlined line in the gameplay-title style (44 units, a 5.5-unit border, at a raster scale), built from
/// cached glyphs: each character is rasterized once per font and scale, and a line only places them and merges
/// their masks (the border of a merged line is the union of its glyphs' borders). The surface is as wide as the
/// line, in the gameplay title's vertical frame (64 units tall, the same baseline), so a line and its single
/// characters share rows. Unlike the gameplay title, a long line is not squeezed: callers fit it into their box.
/// </summary>
public static class GlyphLineRasterizer
{
    private const float FontUnits = 44, OutlineUnits = 5.5f, LineUnits = 64, CentreUnits = 32;

    // ponytail: never evicted; a few hundred glyphs per font and scale (UI text, names, titles).
    private static readonly ConcurrentDictionary<(string Font, uint Scale, int Scalar), GlyphMasks> Glyphs = new();

    public static RgbaTextSurface Render(string fontPath, string text, uint outlineRgb, uint rasterScale)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rasterScale);
        var font = Path.GetFullPath(fontPath);
        var placed = new List<(int X, GlyphMasks Glyph)>();
        var pen = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = Glyphs.GetOrAdd((font, rasterScale, rune.Value), static key => NativeVerticalTextRasterizer.RenderGlyph(
                key.Font, key.Scalar, FontUnits * key.Scale, (uint)(OutlineUnits * key.Scale + 0.5f)));
            placed.Add(((int)pen + glyph.Left, glyph)); // whole pixels, as the title profiles place them
            pen += glyph.Advance;
        }
        var height = (int)(LineUnits * rasterScale);
        if (placed.Count == 0)
            return new RgbaTextSurface(1, (uint)height, new byte[height * 4]);
        var first = placed[0].Glyph;
        var baseline = (int)(CentreUnits * rasterScale + (first.Ascender + first.Descender) * 0.5f);
        var left = placed.Min(static item => item.X);
        var width = Math.Max(1, placed.Max(static item => item.X + item.Glyph.Width) - left);
        var fill = new byte[width * height];
        var outline = new byte[width * height];
        foreach (var (x, glyph) in placed)
            for (var row = 0; row < glyph.Height; row++)
            {
                var y = baseline - glyph.Top + row;
                if (y < 0 || y >= height)
                    continue;
                var source = row * glyph.Width;
                var destination = y * width + x - left;
                for (var column = 0; column < glyph.Width; column++)
                {
                    fill[destination + column] = Math.Max(fill[destination + column], glyph.Fill[source + column]);
                    outline[destination + column] = Math.Max(outline[destination + column], glyph.Outline[source + column]);
                }
            }
        // White fill over the coloured border, premultiplied (as the title profiles compose them).
        var pixels = new byte[width * height * 4];
        uint red = (outlineRgb >> 16) & 255, green = (outlineRgb >> 8) & 255, blue = outlineRgb & 255;
        for (var index = 0; index < fill.Length; index++)
        {
            if (outline[index] == 0)
                continue; // the border covers the fill: nothing here
            uint body = fill[index];
            var border = (outline[index] * (255 - body) + 127) / 255;
            pixels[index * 4] = (byte)Math.Min(255, body + (red * border + 127) / 255);
            pixels[index * 4 + 1] = (byte)Math.Min(255, body + (green * border + 127) / 255);
            pixels[index * 4 + 2] = (byte)Math.Min(255, body + (blue * border + 127) / 255);
            pixels[index * 4 + 3] = (byte)Math.Min(255, body + border);
        }
        return new RgbaTextSurface((uint)width, (uint)height, pixels);
    }
}
