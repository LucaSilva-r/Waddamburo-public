using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Waddamburo.Formats.Text;

/// <summary>
/// A <see cref="TextStyle"/> as skins write it (text.json's styles, a movie.json text field's own settings):
/// <code>
/// { "font": "pop", "size": 64, "spacing": -6, "lineHeight": 1.1, "slant": 0.12,
///   "align": "center", "valign": "center", "fit": "squeeze", "vertical": false,
///   "layers": [ { "grow": 17, "offset": [4, 5], "blur": 6, "color": "#000000b0" },
///               { "grow": 9, "color": "#e01818" },
///               { "color": ["#ffffff", "#c8c8d0"] } ] }
/// </code>
/// Colours are "#rrggbb" or "#rrggbbaa", or [top, bottom] for a gradient over each line. "color" outside the layers
/// sets the front layer's (the fill). Settings merge: a field's own over its style's over the defaults.
/// </summary>
public static class TextStyleJson
{
    /// <summary>The style keys (a text field may set any of them, besides its own rect, style, text and bind).</summary>
    public static readonly ImmutableHashSet<string> Keys =
        ["font", "size", "spacing", "lineHeight", "slant", "align", "valign", "fit", "vertical", "layers", "color"];

    /// <summary><paramref name="over"/>'s settings over <paramref name="under"/>'s (a new object; "color" applies last).</summary>
    public static JsonObject Merge(JsonObject under, JsonObject over)
    {
        var merged = (JsonObject)under.DeepClone();
        foreach (var (key, value) in over)
            if (Keys.Contains(key))
                merged[key] = value?.DeepClone();
        // New layers bring their own colours: a "color" from underneath does not repaint them.
        if (over.ContainsKey("layers") && !over.ContainsKey("color"))
            merged.Remove("color");
        return merged;
    }

    /// <summary>Reads a merged style. A mistake throws <see cref="FormatException"/> naming the setting.</summary>
    public static TextStyle Parse(JsonObject style)
    {
        foreach (var (key, _) in style)
            if (!Keys.Contains(key))
                throw new FormatException($"\"{key}\" is no text setting (one of {string.Join(", ", Keys.Order(StringComparer.Ordinal))}).");
        var layers = style["layers"] is JsonArray list
            ? list.Select((layer, index) => parseLayer(layer as JsonObject ?? throw new FormatException($"layers[{index}] is not an object."), index)).ToList()
            : [new TextLayer(Top: 0x000000ff, Bottom: 0x000000ff)];
        if (layers.Count is 0 or > 16)
            throw new FormatException("A text style has 1 to 16 layers.");
        if (style["color"] is { } fill)
        {
            var (top, bottom) = colours(fill, "color");
            layers[^1] = layers[^1] with { Top = top, Bottom = bottom };
        }
        var size = number(style, "size", 24);
        if (size is < 1 or > 512)
            throw new FormatException("\"size\" is 1 to 512.");
        return new TextStyle(
            style["font"]?.GetValue<string>() ?? "game",
            size,
            [.. layers],
            number(style, "spacing", 0),
            number(style, "lineHeight", 0),
            number(style, "slant", 0),
            align(style, "align", TextAlign.Start),
            align(style, "valign", TextAlign.Center),
            style["fit"]?.GetValue<string>() switch
            {
                null or "shrink" => TextFit.Shrink,
                "clip" => TextFit.Clip,
                "squeeze" => TextFit.Squeeze,
                "wrap" => TextFit.Wrap,
                var other => throw new FormatException($"\"fit\" is clip, shrink, squeeze or wrap, not \"{other}\"."),
            },
            style["vertical"]?.GetValue<bool>() ?? false);
    }

    /// <summary>"#rrggbb" or "#rrggbbaa" as 0xRRGGBBAA.</summary>
    public static uint Colour(string text, string what)
    {
        if (text.Length is 7 or 9 && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return text.Length == 7 ? value << 8 | 0xff : value;
        throw new FormatException($"\"{what}\" needs a colour like \"#ffcc00\" or \"#ffcc0080\", not \"{text}\".");
    }

    private static TextLayer parseLayer(JsonObject layer, int index)
    {
        foreach (var (key, _) in layer)
            if (key is not ("grow" or "offset" or "blur" or "color"))
                throw new FormatException($"layers[{index}]: \"{key}\" is no layer setting (grow, offset, blur, color).");
        float x = 0, y = 0;
        if (layer["offset"] is JsonArray { Count: 2 } offset)
            (x, y) = (offset[0]!.GetValue<float>(), offset[1]!.GetValue<float>());
        else if (layer["offset"] is not null)
            throw new FormatException($"layers[{index}]: \"offset\" is [x, y].");
        var (top, bottom) = layer["color"] is { } colour ? colours(colour, $"layers[{index}].color") : (0xffffffffu, 0xffffffffu);
        var grow = number(layer, "grow", 0);
        var blur = number(layer, "blur", 0);
        if (grow is < 0 or > 128 || blur is < 0 or > 128)
            throw new FormatException($"layers[{index}]: \"grow\" and \"blur\" are 0 to 128.");
        return new TextLayer(grow, x, y, blur, top, bottom);
    }

    private static (uint Top, uint Bottom) colours(JsonNode node, string what) => node switch
    {
        JsonArray { Count: 2 } pair => (Colour(pair[0]!.GetValue<string>(), what), Colour(pair[1]!.GetValue<string>(), what)),
        JsonValue value when value.TryGetValue<string>(out var text) => (Colour(text, what), Colour(text, what)),
        _ => throw new FormatException($"\"{what}\" is a colour or [top, bottom]."),
    };

    private static float number(JsonObject node, string key, float otherwise) => node[key] switch
    {
        null => otherwise,
        JsonValue value when value.TryGetValue<double>(out var number) && double.IsFinite(number) => (float)number,
        JsonValue value when value.TryGetValue<float>(out var single) && float.IsFinite(single) => single, // set from code
        _ => throw new FormatException($"\"{key}\" is a number."),
    };

    private static TextAlign align(JsonObject node, string key, TextAlign otherwise) => node[key]?.GetValue<string>() switch
    {
        null => otherwise,
        "left" or "top" or "start" => TextAlign.Start,
        "center" or "middle" => TextAlign.Center,
        "right" or "bottom" or "end" => TextAlign.End,
        var other => throw new FormatException($"\"{key}\" is left/top, center or right/bottom, not \"{other}\"."),
    };
}
