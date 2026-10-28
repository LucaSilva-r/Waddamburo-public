namespace Waddamburo.Platform.Sdl.Text;

/// <summary>Horizontal lettering for the rotated Lumen song boards, using the game's outlined glyph pipeline.</summary>
public static class HorizontalSongTitleRasterizer
{
    // The vertical menu's authored board transform has different horizontal/vertical factors.
    // Compensate before it is applied so the original font keeps its proportions on screen.
    public const float VerticalBoardAspect = .65f / 1.1f;

    public static int ScrollingWidth(string font, string title, string? subtitle, int height, float horizontalAspect = 1, float? titleLineHeight = null)
    {
        validateAspect(horizontalAspect);
        var titleHeight = titleLineHeight ?? (subtitle is { Length: > 0 } ? height * .64f : height);
        var width = GlyphLineRasterizer.Measure(font, title, 1).Width * titleHeight / 64 * horizontalAspect + 12;
        if (subtitle is { Length: > 0 })
            width = Math.Max(width, GlyphLineRasterizer.Measure(font, subtitle, 1).Width * height * .34f / 64 * horizontalAspect + 16);
        // The largest supported raster scale is four: keep the uploaded side within 16384 pixels.
        return (int)Math.Clamp(MathF.Ceiling(width), 400, 4096);
    }

    /// <summary>Width of the rendered glyph line, excluding transparent canvas padding.</summary>
    public static float ContentWidth(string font, string title, int width, int height, float horizontalAspect = 1)
    {
        validateAspect(horizontalAspect);
        var source = GlyphLineRasterizer.Measure(font, title, 2);
        var factor = Math.Min((width - 12) / (source.Width * horizontalAspect), height / (float)source.Height);
        return MathF.Round(source.Width * factor * horizontalAspect);
    }

    public static RgbaTextSurface Render(string font, string title, string? subtitle, int width, int height,
        uint outline, uint scale = 1, float horizontalAspect = 1, bool centered = false, float? titleLineHeight = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(scale);
        validateAspect(horizontalAspect);
        var w = checked(width * (int)scale);
        var h = checked(height * (int)scale);
        var pixels = new byte[checked(w * h * 4)];
        line(title, 6, subtitle is { Length: > 0 } ? 2 : 0, width - 12,
            titleLineHeight ?? (subtitle is { Length: > 0 } ? height * .64f : height), outline);
        if (subtitle is { Length: > 0 })
        {
            var top = titleLineHeight is { } fixedHeight ? fixedHeight + 2 : height * .64f;
            line(subtitle, 8, top, width - 16, height - top - 2, 0);
        }
        return new((uint)w, (uint)h, pixels);

        void line(string text, float x, float y, float boxWidth, float boxHeight, uint border)
        {
            var source = GlyphLineRasterizer.Render(font, text, border, Math.Max(scale, 2U));
            var factor = Math.Min(boxWidth * scale / (source.Width * horizontalAspect), boxHeight * scale / source.Height);
            var outWidth = Math.Max(1, (int)MathF.Round(source.Width * factor * horizontalAspect));
            var outHeight = Math.Max(1, (int)MathF.Round(source.Height * factor));
            var ox = (int)MathF.Round(x * scale + (centered ? (boxWidth * scale - outWidth) / 2 : 0));
            var oy = (int)(y * scale + (boxHeight * scale - outHeight) / 2);
            for (var py = 0; py < outHeight && oy + py < h; py++)
                for (var px = 0; px < outWidth && ox + px < w; px++)
                {
                    var sx = Math.Clamp((px + .5f) / (factor * horizontalAspect) - .5f, 0, source.Width - 1);
                    var sy = Math.Clamp((py + .5f) / factor - .5f, 0, source.Height - 1);
                    var x0 = (int)sx; var y0 = (int)sy;
                    var x1 = Math.Min(x0 + 1, (int)source.Width - 1);
                    var y1 = Math.Min(y0 + 1, (int)source.Height - 1);
                    var fx = sx - x0; var fy = sy - y0;
                    var destination = ((oy + py) * w + ox + px) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var top = source.Pixels[(y0 * (int)source.Width + x0) * 4 + channel] * (1 - fx)
                            + source.Pixels[(y0 * (int)source.Width + x1) * 4 + channel] * fx;
                        var bottom = source.Pixels[(y1 * (int)source.Width + x0) * 4 + channel] * (1 - fx)
                            + source.Pixels[(y1 * (int)source.Width + x1) * 4 + channel] * fx;
                        pixels[destination + channel] = (byte)MathF.Round(top * (1 - fy) + bottom * fy);
                    }
                }
        }
    }

    private static void validateAspect(float aspect)
    {
        if (!float.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
    }

    /// <summary>Stores a horizontal line in a portrait slot; the edited Lumen rotates it back on screen.</summary>
    public static RgbaTextSurface RotateCounterClockwise(RgbaTextSurface surface)
    {
        var w = checked((int)surface.Width); var h = checked((int)surface.Height);
        var pixels = new byte[surface.Pixels.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                surface.Pixels.AsSpan((y * w + x) * 4, 4).CopyTo(pixels.AsSpan(((w - 1 - x) * h + y) * 4, 4));
        return new(surface.Height, surface.Width, pixels);
    }
}
