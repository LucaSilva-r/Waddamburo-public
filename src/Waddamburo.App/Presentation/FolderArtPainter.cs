using System.Collections.Concurrent;
using System.Text;
using Waddamburo.Game.SongSelect;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Built-in folder patterns and emblems come from embedded PNGs. Original Songs reuses
/// the local Waiwai Normal-mode illustration when available; description columns are drawn.
/// </summary>
internal static class FolderArtPainter
{
    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    private static readonly ConcurrentDictionary<string, RgbaTextSurface> Images = new();

    /// <summary>The folder front's pattern (any size: drawn over the open front, revealed as it opens).</summary>
    public static RgbaTextSurface Pattern(FolderArt art) => art.Style != FolderArtStyle.Osu ? image(art.Style, "pattern")
        : Environment.GetEnvironmentVariable("WADDAMBURO_OSU_CATEGORY_PATTERN") is { Length: > 0 } preview
        ? Images.GetOrAdd("osu-preview:" + Path.GetFullPath(preview), static key => pinkPattern(key[12..]))
        : Images.GetOrAdd("osu-vivid-pattern", static _ => pinkPattern(image(FolderArtStyle.Osu, "triangles")));

    private static RgbaTextSurface pinkPattern(string path)
    {
        var (width, height, pixels) = Waddamburo.Upscale.PngFile.Read(path);
        return pinkPattern(new RgbaTextSurface(width, height, pixels));
    }

    private static RgbaTextSurface pinkPattern(RgbaTextSurface source)
    {
        var pixels = source.Pixels.ToArray();
        double total = 0, coverage = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            total += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3d * pixels[i + 3];
            coverage += pixels[i + 3];
        }
        var mean = coverage > 0 ? total / coverage : 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3f;
            var shade = Math.Clamp(.25 + .5 * mean / 255 + (luminance - mean) / 28, 0, 1);
            pixels[i] = 255;
            pixels[i + 1] = (byte)Math.Round(10 + 172 * shade);
            pixels[i + 2] = (byte)Math.Round(140 + 104 * shade);
        }
        return new(source.Width, source.Height, pixels);
    }

    internal static string RegisterPeg(byte[] source, int width, int height, byte red, byte green, byte blue)
    {
        var key = $"folder-peg:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source))}:{red:x2}{green:x2}{blue:x2}";
        Images.GetOrAdd(key, _ =>
        {
            var pixels = source.ToArray();
            for (var at = 0; at < pixels.Length; at += 4)
            {
                var coloured = Math.Max(pixels[at], Math.Max(pixels[at + 1], pixels[at + 2])) > 40;
                if (pixels[at + 3] < (coloured ? 100 : 220)) { pixels[at + 3] = 0; continue; }
                pixels[at + 3] = 255;
                if (coloured) { pixels[at] = red; pixels[at + 1] = green; pixels[at + 2] = blue; }
            }
            return new((uint)width, (uint)height, pixels);
        });
        return key;
    }
    internal static RgbaTextSurface Peg(string key) => Images[key];

    /// <summary>The open folder's emblem (any size: drawn into the 192x360 mascot slot).</summary>
    public static RgbaTextSurface Box(FolderArt art) => image(art.Style == FolderArtStyle.Nijiiro ? FolderArtStyle.Original : art.Style, "box");

    /// <summary>Reuses Waiwai's native Normal-mode mascot without distributing its artwork.</summary>
    public static RgbaTextSurface? NormalModeBox(string assetRoot)
    {
        var path = Path.Combine(assetRoot, "waiwai_song_select", "packeddata.ddp");
        if (!File.Exists(path)) return null;
        var archive = DdpArchive.Open(File.ReadAllBytes(path));
        var movie = archive.OpenMovie("waiwai_song_select/waiwai_song_select.lm");
        var document = new LmbEditDocument(movie.Data.Span);
        var definition = document.Definition;
        // Resolve the illustration by its authored folder label and dimensions,
        // rather than relying on a texture index that varies between movies.
        foreach (var sprite in definition.Sprites)
        {
            var label = sprite.Timeline.OfType<LmbFrameLabelCommand>().FirstOrDefault(frame =>
                definition.Strings[(int)frame.StringIndex].Value == SongSelectCatalogView.ModeSwitchLabel);
            if (label is null) continue;
            foreach (var placed in document.Placements(sprite.CharacterId, (int)label.Frame))
            {
                var shape = definition.Shapes.FirstOrDefault(shape => shape.CharacterId == placed.CharacterId);
                if (shape is null) continue;
                foreach (var index in shape.Geometry.Select(geometry => geometry.TextureIndex).Distinct())
                {
                    if (index >= movie.Textures.Length) continue;
                    var texture = NutFile.Parse(movie.Textures[(int)index].Data).Textures[0];
                    if (texture.Width == 192 && texture.Height == 360)
                        return new(texture.Width, texture.Height, NutTextureDecoder.DecodeRgba8(texture));
                }
            }
        }
        return null;
    }

    private static RgbaTextSurface image(FolderArtStyle style, string part) =>
        Images.GetOrAdd($"{style.ToString().ToLowerInvariant()}.{part}", static name =>
        {
            // Default resource names join the folders with dots (ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦Resources.folders.osu.box.png).
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
    public static RgbaTextSurface Description(string lines, string fontPath, uint outlineRgb, int scale, bool squash,
        bool englishParagraph = false)
    {
        // Match the supplied English genre artwork: compact horizontal lines,
        // centred within the native portrait slot rather than vertical Latin columns.
        if (englishParagraph)
            return HorizontalTextParagraph.Render(fontPath, lines, DescriptionWidth, DescriptionHeight,
                outlineRgb, (uint)scale, preferredLines: 11);
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
