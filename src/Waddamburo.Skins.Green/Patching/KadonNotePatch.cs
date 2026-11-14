using Waddamburo.Game;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Waddamburo.Skins.Green.Patching;

/// <summary>
/// OpenTaiko's Kadon (TJA G) has no art in Green's files: the big ka (onp_katsu_dai) and the flying note
/// (onp_kiseki_don_1p) get a purple copy of each of their textures appended (blue keyed to purple; white, black, red and
/// the faces untouched). A Kadon is that movie drawn with its texture namespace moved onto the copies
/// (<see cref="CopiesAt"/>), so the big ka and its flight stay as they are.
/// </summary>
public static class KadonNotePatch
{
    private static readonly string[] MovieNames = ["onp_katsu_dai/onp_katsu_dai.lm", "onp_kiseki_don_1p/onp_kiseki_don_1p.lm"];

    public static bool AppliesTo(LumenMovieContent content) => MovieNames.Any(name =>
        content.Name == name || content.Name.EndsWith("/" + name, StringComparison.Ordinal));

    /// <summary>Where the purple copies start in a patched movie's textures (half of them).</summary>
    public static int CopiesAt(LumenMovieContent content) => content.Textures.Length / 2;

    /// <summary>Appends the purple copies (from the textures as they are now: an upscale when one was swapped in).</summary>
    /// <returns>The copies' pixels.</returns>
    public static IReadOnlyList<byte[]> Apply(LumenMovieContent content)
    {
        var own = content.Textures;
        var copies = new byte[own.Length][];
        var textures = ImmutableArray.CreateBuilder<LumenTextureContent>(own.Length * 2);
        textures.AddRange(own);
        for (var index = 0; index < own.Length; index++)
        {
            var source = own[index].Pixels();
            var pixels = copies[index] = (byte[])source.Clone();
            Recolour(pixels);
            // An upscale's Width/Height are already its own (larger) size.
            textures.Add(new LumenTextureContent(own.Length + index, own[index].Width, own[index].Height,
                ImmutableCollectionsMarshal.AsImmutableArray(pixels)));
        }
        content.Replace(content.Definition, textures.MoveToImmutable());
        return copies;
    }

    /// <summary>
    /// Turns blue pixels (hue 140-270 degrees) purple at their own saturation and value: a hue turn leaves greys,
    /// white and black as they are, and the faint blue of edges into them turns with the rest (no blue fringe).
    /// RGBA, straight alpha.
    /// </summary>
    public static void Recolour(Span<byte> rgba)
    {
        for (var at = 0; at + 3 < rgba.Length; at += 4)
        {
            float r = rgba[at] / 255f, g = rgba[at + 1] / 255f, b = rgba[at + 2] / 255f;
            var max = MathF.Max(r, MathF.Max(g, b));
            var min = MathF.Min(r, MathF.Min(g, b));
            var chroma = max - min;
            if (chroma < 0.01f) continue;
            var hue = max == r ? 60 * ((g - b) / chroma % 6) : max == g ? 60 * ((b - r) / chroma + 2) : 60 * ((r - g) / chroma + 4);
            if (hue < 0) hue += 360;
            // Full strength from 160 to 250 degrees (block compression drifts the edges towards cyan), fading out to 140 and 270.
            var amount = Math.Clamp(MathF.Min(hue - 140, 270 - hue) / 20, 0, 1);
            if (amount == 0) continue;
            // Hue +85 degrees (Ka's cyan-blue to OpenTaiko's purple), same saturation and value.
            var (nr, ng, nb) = fromHue((hue + 85) % 360, chroma, min);
            rgba[at] = (byte)Math.Round(255 * (r + (nr - r) * amount));
            rgba[at + 1] = (byte)Math.Round(255 * (g + (ng - g) * amount));
            rgba[at + 2] = (byte)Math.Round(255 * (b + (nb - b) * amount));
        }
    }

    private static (float R, float G, float B) fromHue(float hue, float chroma, float min)
    {
        var x = chroma * (1 - MathF.Abs(hue / 60 % 2 - 1));
        var (r, g, b) = (hue / 60) switch
        {
            < 1 => (chroma, x, 0f),
            < 2 => (x, chroma, 0f),
            < 3 => (0f, chroma, x),
            < 4 => (0f, x, chroma),
            < 5 => (x, 0f, chroma),
            _ => (chroma, 0f, x),
        };
        return (r + min, g + min, b + min);
    }
}
