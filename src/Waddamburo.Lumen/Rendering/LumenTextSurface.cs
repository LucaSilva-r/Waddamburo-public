using System.Globalization;
using System.Text.RegularExpressions;

namespace Waddamburo.Lumen.Rendering;

/// <summary>
/// Native surface keys for dynamic text fields: the text plus how to draw it, parsed back by the host's
/// text rasterizer. Key: <c>text:{align}:{rgb}:{width}:{height}:{size}:{text}</c>.
/// </summary>
public static partial class LumenTextSurface
{
    public const string Prefix = "text:";

    public static LumenNativeSurfaceKey Key(string text, float width, float height, float size, char align, uint rgb) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{align}:{rgb:x6}:{width}:{height}:{size}:{text}"));

    public static bool TryParse(LumenNativeSurfaceKey key, out string text, out float width, out float height,
        out float size, out char align, out uint rgb)
    {
        text = "";
        width = height = size = 0;
        align = 'l';
        rgb = 0;
        var parts = key.Value.StartsWith(Prefix, StringComparison.Ordinal) ? key.Value[Prefix.Length..].Split(':', 6) : [];
        if (parts is not [[var a], var colour, var w, var h, var s, var body]
            || !uint.TryParse(colour, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)
            || !float.TryParse(w, CultureInfo.InvariantCulture, out width)
            || !float.TryParse(h, CultureInfo.InvariantCulture, out height)
            || !float.TryParse(s, CultureInfo.InvariantCulture, out size))
            return false;
        align = a;
        text = body;
        return true;
    }

    /// <summary>The initial HTML's paragraph alignment: 'l', 'c' or 'r'.</summary>
    public static char Align(string html) => AlignPattern().Match(html) is { Success: true } match
        ? match.Groups[1].Value.ToLowerInvariant() switch { "right" => 'r', "center" => 'c', _ => 'l' } : 'l';

    /// <summary>The initial HTML's font colour (0xRRGGBB), black when absent.</summary>
    public static uint Colour(string html) => ColourPattern().Match(html) is { Success: true } match
        ? uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex("align=\"(\\w+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AlignPattern();

    [GeneratedRegex("color=\"#([0-9a-fA-F]{6})\"")]
    private static partial Regex ColourPattern();
}
