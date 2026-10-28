namespace Waddamburo.Platform.Sdl.Text;

/// <summary>Outlined horizontal paragraphs, fitted by wrapping before reducing the line height.</summary>
public static class HorizontalTextParagraph
{
    public static RgbaTextSurface Render(string font, string text, int width, int height, uint outlineRgb = 0,
        uint scale = 1, uint fillRgb = 0xffffff, int preferredLines = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredLines);
        ArgumentOutOfRangeException.ThrowIfZero(scale);
        var lineHeight = Math.Max(1, height / preferredLines);
        var lines = wrap(lineHeight);
        while (lines.Count * lineHeight > height && lineHeight > 1)
        {
            lineHeight = Math.Max(1, Math.Min(lineHeight - 1, height / lines.Count));
            lines = wrap(lineHeight);
        }
        var w = checked(width * (int)scale);
        var h = checked(height * (int)scale);
        var pixels = new byte[checked(w * h * 4)];
        var top = Math.Max(0, (h - lines.Count * lineHeight * (int)scale) / 2);
        foreach (var line in lines)
        {
            var rendered = HorizontalSongTitleRasterizer.Render(font, line, null, width, lineHeight, outlineRgb, scale);
            var first = w; var last = -1;
            for (var y = 0; y < rendered.Height; y++)
                for (var x = 0; x < w; x++)
                    if (rendered.Pixels[(y * w + x) * 4 + 3] != 0)
                    {
                        first = Math.Min(first, x); last = Math.Max(last, x);
                    }
            if (last >= first)
                for (var y = 0; y < rendered.Height && top + y < h; y++)
                    rendered.Pixels.AsSpan((y * w + first) * 4, (last - first + 1) * 4)
                        .CopyTo(pixels.AsSpan(((top + y) * w + (w - (last - first + 1)) / 2) * 4));
            top += lineHeight * (int)scale;
        }
        if (fillRgb != 0xffffff && outlineRgb == 0)
            for (var at = 0; at < pixels.Length; at += 4)
            {
                pixels[at] = (byte)(pixels[at] * ((fillRgb >> 16) & 255) / 255);
                pixels[at + 1] = (byte)(pixels[at + 1] * ((fillRgb >> 8) & 255) / 255);
                pixels[at + 2] = (byte)(pixels[at + 2] * (fillRgb & 255) / 255);
            }
        return new((uint)w, (uint)h, pixels);

        List<string> wrap(int rowHeight)
        {
            var result = new List<string>();
            var current = "";
            float measure(string value) => GlyphLineRasterizer.Measure(font, value, 1).Width * rowHeight / 64f;
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var trial = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && measure(trial) > width - 12)
                {
                    result.Add(current); current = "";
                }
                if (measure(word) <= width - 12)
                    current = current.Length == 0 ? word : current + " " + word;
                else
                    foreach (var rune in word.EnumerateRunes())
                    {
                        if (current.Length > 0 && measure(current + rune) > width - 12)
                        {
                            result.Add(current); current = "";
                        }
                        current += rune;
                    }
            }
            if (current.Length > 0) result.Add(current);
            return result;
        }
    }
}
