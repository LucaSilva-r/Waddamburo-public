using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The name art of a series' challenge courses: nutdata/(cabinet)/appendable/NN/dojochallenge_(long|short|mini)
/// NUTs, concatenated in the order they shipped. The counts match the courses above level 100 in the course
/// lists (Green 28, Blue 14, Red 13), so the n-th challenge course has the n-th texture.
/// </summary>
/// <remarks>The player's own series (CustomDans, "custom:" ids) have no art: their course name is drawn instead, at the size
/// of Green's plate of that kind. So is a stock course whose plate this Green lacks (an install without that update).</remarks>
internal sealed class DanChallengeArt(NutData nutData, Waddamburo.Platform.Sdl.SdlApplication application, string fontPath)
{
    /// <summary>Challenge names, by (series, challenge number): set as their boards are listed.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<(string Series, int Challenge), string> Names { get; } = new();

    private readonly Dictionary<string, RenderTextureId> _drawn = [];

    private static readonly Dictionary<string, (string Cabinet, string[] Folders)> Series = new(StringComparer.Ordinal)
    {
        ["green"] = ("S11100-1", ["00", "01", "10", "11", "12"]),
        ["blue"] = ("S10100-1", ["00"]),
        ["yellow"] = ("ST9100-1", ["00", "04", "06", "08"]),
        ["red"] = ("ST8100-1", ["05", "06", "07"]),
    };

    /// <summary>A course's art, <paramref name="challenge"/> counting from 1 in its series.</summary>
    public static LumenNativeSurfaceKey Key(string series, int challenge, string kind) => new($"dan-challenge:{series}:{challenge}:{kind}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("dan-challenge:", StringComparison.Ordinal))
            return null;
        // dan-challenge:<series>:<n>:<kind>; a custom series' id has a colon of its own.
        var parts = key.Value.Split(':');
        if (parts.Length < 4 || !int.TryParse(parts[^2], out var challenge) || challenge <= 0)
            return null;
        var series = string.Join(':', parts[1..^2]);
        if (!Series.TryGetValue(series, out var source))
            return drawn(key.Value, Names.GetValueOrDefault((series, challenge), ""), parts[^1]);
        // The n-th challenge is the n-th texture of the folders' NUTs, one after another.
        var index = challenge - 1;
        foreach (var folder in source.Folders)
        {
            var path = $"{source.Cabinet}/appendable/{folder}/dojochallenge_{parts[^1]}/dojochallenge_{parts[^1]}.nut";
            var count = nutData.Textures(path).Length;
            if (index < count)
                return nutData.Texture(path, index);
            index -= count;
        }
        // ponytail: a missing folder before the last shifts the later plates; needs each folder's count without its file.
        return drawn(key.Value, Names.GetValueOrDefault((series, challenge), ""), parts[^1]);
    }

    // Where each kind's plate has its name (fractions of the plate: left, top, right, bottom), inside its paper.
    private static readonly Dictionary<string, (float X0, float Y0, float X1, float Y1)> TextArea = new(StringComparer.Ordinal)
    {
        ["long"] = (.15f, .03f, .85f, .97f),
        ["short"] = (.12f, .25f, .93f, .8f),
        ["mini"] = (.08f, .25f, .92f, .8f),
    };

    // Green's first plate of that kind with its name painted out, the custom name written on it in the plates' ink.
    private RenderTextureId drawn(string key, string name, string kind)
    {
        if (_drawn.TryGetValue(key, out var texture))
            return texture;
        var plate = nutData.Textures($"S11100-1/appendable/00/dojochallenge_{kind}/dojochallenge_{kind}.nut").FirstOrDefault();
        int width = plate?.Width ?? 312, height = plate?.Height ?? 120;
        const int scale = 2;
        var canvas = new VectorCanvas(width, height, scale, fontPath);
        var area = TextArea.TryGetValue(kind, out var known) ? known : (X0: 0f, Y0: 0f, X1: 1f, Y1: 1f);
        if (plate is not null)
        {
            var paper = Waddamburo.Formats.Nut.NutTextureDecoder.DecodeRgba8(plate);
            PaintOut(paper, width, height, area);
            for (var y = 0; y < canvas.PixelHeight; y++)
                for (var x = 0; x < canvas.PixelWidth; x++)
                    Array.Copy(paper, (y / scale * width + x / scale) * 4, canvas.Pixels, (y * canvas.PixelWidth + x) * 4, 4);
        }
        // The short sign's name sits on its middle (official plates), not on the painted-out area, which starts right of
        // the sign's torn left edge.
        var middle = kind == "short" ? .5f : (area.X0 + area.X1) / 2;
        float centreX = middle * width, centreY = (area.Y0 + area.Y1) / 2 * height;
        float boxWidth = 2 * Math.Min(middle - area.X0, area.X1 - middle) * width, boxHeight = (area.Y1 - area.Y0) * height;
        // As the official plates: white letters in a black outline, top to bottom on the long (portrait) one, centred
        // on their ink (the rasterizer starts a title at the top of its strip).
        if (name.Trim().Length > 0 && kind == "long")
        {
            var title = Waddamburo.Platform.Sdl.Text.NativeVerticalTextRasterizer.RenderSongTitle(fontPath, name, null,
                Waddamburo.Platform.Sdl.Text.SongTitleTextProfile.Compact, 0x000000, scale);
            if (ink(title) is { } part)
                canvas.Image(title, part, centreX, centreY, boxWidth, boxHeight, upscale: false);
        }
        else
            canvas.Text(name, centreX, centreY, boxWidth, boxHeight * .6f);
        return _drawn[key] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    // The box around a surface's visible pixels (null: none).
    private static (int X, int Y, int W, int H)? ink(Waddamburo.Platform.Sdl.Text.RgbaTextSurface surface)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, width = (int)surface.Width;
        for (var y = 0; y < surface.Height; y++)
            for (var x = 0; x < width; x++)
                if (surface.Pixels[(y * width + x) * 4 + 3] > 0)
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        return right < 0 ? null : (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Paints a plate's name out (RGBA, in place): inside <paramref name="area"/>, its black letters and their white outline
    /// (grown 2 px for their soft edges) take the paper of their own column, the column's other pixels tiled over them.
    /// </summary>
    public static void PaintOut(byte[] rgba, int width, int height, (float X0, float Y0, float X1, float Y1) area)
    {
        int x0 = (int)(area.X0 * width), y0 = (int)(area.Y0 * height), x1 = (int)(area.X1 * width), y1 = (int)(area.Y1 * height);
        var mask = new bool[width * height];
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 4;
                int high = Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])), low = Math.Min(rgba[i], Math.Min(rgba[i + 1], rgba[i + 2]));
                mask[y * width + x] = rgba[i + 3] > 0 && (high < 110 || low > 225);
            }
        for (var pass = 0; pass < 2; pass++)
        {
            var grown = (bool[])mask.Clone();
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    if (!mask[y * width + x] && (x > x0 && mask[y * width + x - 1] || x + 1 < x1 && mask[y * width + x + 1]
                        || y > y0 && mask[(y - 1) * width + x] || y + 1 < y1 && mask[(y + 1) * width + x]))
                        grown[y * width + x] = true;
            mask = grown;
        }
        for (var x = x0; x < x1; x++)
        {
            var paper = Enumerable.Range(y0, y1 - y0).Where(y => !mask[y * width + x]).ToArray();
            if (paper.Length == 0)
                continue;
            var next = 0;
            for (var y = y0; y < y1; y++)
                if (mask[y * width + x])
                    Array.Copy(rgba, (paper[next++ % paper.Length] * width + x) * 4, rgba, (y * width + x) * 4, 4);
        }
    }
}
