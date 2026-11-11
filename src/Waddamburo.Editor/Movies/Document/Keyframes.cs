using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;

namespace Waddamburo.Editor;

public sealed partial class MovieDocument
{
    /// <summary>
    /// The key of <paramref name="layer"/> on <paramref name="frame"/>, made when there is none: a new key starts
    /// with the values the layer has on that frame, so adding it changes nothing by itself.
    /// </summary>
    public static JsonObject KeyAt(JsonObject layer, int frame)
    {
        var keys = layer["keys"] as JsonArray ?? throw new FormatException("A layer has no keys.");
        var sorted = keys.OfType<JsonObject>().OrderBy(static key => (int)key["frame"]!).ToList();
        if (sorted.FirstOrDefault(key => (int)key["frame"]! == frame) is { } existing)
            return existing;
        var before = sorted.LastOrDefault(key => (int)key["frame"]! < frame);
        var after = sorted.FirstOrDefault(key => (int)key["frame"]! > frame);
        var created = new JsonObject { ["frame"] = frame };
        // Between two keys: where the first key's ease puts the values on this frame.
        var between = before is not null && after is not null && (bool?)before["hold"] != true;
        var t = between ? MoviePackage.Ease((string?)before!["ease"], (frame - (int)before["frame"]!) / (double)((int)after!["frame"]! - (int)before["frame"]!)) : 0;
        foreach (var name in Properties)
        {
            var from = Value(before ?? after!, name)!.Value;
            var value = between ? from + (Value(after!, name)!.Value - from) * t : from;
            if (value != Default(name))
                created[name] = Math.Round(value, 3);
        }
        var tint = Tint(before ?? after!);
        if (between)
        {
            var to = Tint(after!);
            tint = (tint.Red + (to.Red - tint.Red) * t, tint.Green + (to.Green - tint.Green) * t, tint.Blue + (to.Blue - tint.Blue) * t);
        }
        if (tint != (1, 1, 1))
            SetTint(created, tint.Red, tint.Green, tint.Blue);
        keys.Insert(sorted.Count(key => (int)key["frame"]! < frame), created);
        return created;
    }

    /// <summary>A key's tint (what the clip's colours are multiplied by; 1, 1, 1 leaves them).</summary>
    public static (double Red, double Green, double Blue) Tint(JsonObject key) =>
        key["tint"] is JsonArray { Count: 3 } tint ? ((double)tint[0]!, (double)tint[1]!, (double)tint[2]!) : (1, 1, 1);

    public static void SetTint(JsonObject key, double red, double green, double blue)
    {
        if ((red, green, blue) == (1, 1, 1))
            key.Remove("tint");
        else
            key["tint"] = new JsonArray(Math.Round(red, 3), Math.Round(green, 3), Math.Round(blue, 3));
    }

    /// <summary>How a key's values run to the next key: one of MoviePackage.Eases, or "hold" (they stay until it).</summary>
    public static string Ease(JsonObject key) => (bool?)key["hold"] == true ? "hold" : (string?)key["ease"] ?? "linear";

    public static void SetEase(JsonObject key, string ease)
    {
        if (ease != "hold" && !MoviePackage.Eases.Contains(ease))
            throw new InvalidOperationException($"There is no ease '{ease}'.");
        key.Remove("hold");
        key.Remove("ease");
        if (ease == "hold")
            key["hold"] = true;
        else if (ease != "linear")
            key["ease"] = ease;
    }

    /// <summary>The numbers a key holds (besides its frame and tint).</summary>
    public static readonly string[] Properties = ["x", "y", "scaleX", "scaleY", "rotation", "alpha"];

    public static double Default(string property) => property is "scaleX" or "scaleY" or "alpha" ? 1 : 0;

    /// <summary>A key's value of a property, with "scale" standing for both scales when they are not given.</summary>
    public static double? Value(JsonObject key, string property) =>
        number(key[property]) ?? (property is "scaleX" or "scaleY" ? number(key["scale"]) : null) ?? Default(property);

    // A number however it got into the document (parsed, or set as a float or an int by an edit).
    private static double? number(JsonNode? node) => node is null ? null
        : double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static void Set(JsonObject key, string property, double value)
    {
        if (property is "scaleX" or "scaleY" && key["scale"] is { } both)
        {
            // "scale" is split so one axis can change.
            var uniform = (double)both;
            key.Remove("scale");
            key["scaleX"] = uniform;
            key["scaleY"] = uniform;
        }
        key[property] = Math.Round(value, 3);
    }
}
