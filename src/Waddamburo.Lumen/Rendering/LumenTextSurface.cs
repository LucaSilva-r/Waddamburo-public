using System.Globalization;
using System.Text.RegularExpressions;

namespace Waddamburo.Lumen.Rendering;

/// <summary>
/// Native surface keys for dynamic text fields: the text plus how to draw it, parsed back by the host's
/// text rasterizer. Key: <c>text:{align}:{rgb}:{width}:{height}:{size}:{settings}:{text}</c>; settings is the number
/// of the field's own text settings (<see cref="Settings"/>; 0: none), the JSON a package's field carries after its
/// paragraph (<c>&lt;!--wd {...}--&gt;</c>: a style, its settings, a text, a bind).
/// </summary>
public static partial class LumenTextSurface
{
    public const string Prefix = "text:";
    private const string SettingsMark = "<!--wd ";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_settingsIds = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> s_settings = new();

    public static LumenNativeSurfaceKey Key(string text, float width, float height, float size, char align, uint rgb, int settings = 0) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{align}:{rgb:x6}:{width}:{height}:{size}:{settings}:{text}"));

    /// <summary>The field's own text settings (JSON) in its HTML, or null.</summary>
    public static string? SettingsOf(string html)
    {
        var start = html.IndexOf(SettingsMark, StringComparison.Ordinal);
        var end = start < 0 ? -1 : html.IndexOf("-->", start, StringComparison.Ordinal);
        return end < 0 ? null : html[(start + SettingsMark.Length)..end];
    }

    /// <summary>A number for these settings, the same for the same JSON (keys stay short).</summary>
    public static int SettingsId(string json) => s_settingsIds.GetOrAdd(json, static json =>
    {
        lock (s_settings)
        {
            var id = s_settings.Count + 1;
            s_settings[id] = json;
            return id;
        }
    });

    /// <summary>
    /// How far a field with these settings draws out of its box on every side (its style's borders and shadow), in the
    /// box's units: the host that resolves styles sets it. The field's quad grows by it; the text still fills the box.
    /// </summary>
    public static Func<int, float>? Margin { get; set; }

    /// <summary>The settings a key's number stands for; null for 0 or an unknown one.</summary>
    public static string? Settings(int id) => s_settings.GetValueOrDefault(id);

    public static bool TryParse(LumenNativeSurfaceKey key, out string text, out float width, out float height,
        out float size, out char align, out uint rgb) => TryParse(key, out text, out width, out height, out size, out align, out rgb, out _);

    public static bool TryParse(LumenNativeSurfaceKey key, out string text, out float width, out float height,
        out float size, out char align, out uint rgb, out int settings)
    {
        text = "";
        width = height = size = 0;
        align = 'l';
        rgb = 0;
        settings = 0;
        var parts = key.Value.StartsWith(Prefix, StringComparison.Ordinal) ? key.Value[Prefix.Length..].Split(':', 7) : [];
        if (parts is not [[var a], var colour, var w, var h, var s, var id, var body]
            || !int.TryParse(id, CultureInfo.InvariantCulture, out settings)
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
