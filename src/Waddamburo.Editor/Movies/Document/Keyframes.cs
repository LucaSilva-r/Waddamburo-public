using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;

namespace Waddamburo.Editor;

public sealed partial class MovieDocument
{
    /// <summary>The channels a key can hold: the numbers, and the tint.</summary>
    public static readonly string[] Channels = ["x", "y", "scaleX", "scaleY", "rotation", "alpha", "tint"];

    /// <summary>Whether a key holds a channel (a key only speaks for the channels it names).</summary>
    public static bool Has(JsonObject key, string channel) => key.ContainsKey(channel) || channel is "scaleX" or "scaleY" && key.ContainsKey("scale");

    private static List<JsonObject> sorted(JsonObject layer) =>
        [.. (layer["keys"] as JsonArray ?? throw new FormatException("A layer has no keys.")).OfType<JsonObject>().OrderBy(static key => (int)key["frame"]!)];

    // A channel's values on a frame (one number, or the tint's three): from the key that names it at or before the
    // frame to the next that does, by the first one's ease. Before its first key it is already there.
    private static double[] sample(List<JsonObject> keys, string channel, int frame)
    {
        static double[] of(JsonObject key, string channel) => channel == "tint" ? [Tint(key).Red, Tint(key).Green, Tint(key).Blue] : [Value(key, channel)!.Value];
        var held = keys.Where(key => Has(key, channel)).ToList();
        var from = held.LastOrDefault(key => (int)key["frame"]! <= frame);
        var to = held.FirstOrDefault(key => (int)key["frame"]! > frame);
        if (from is null)
            return to is null ? channel == "tint" ? [1, 1, 1] : [Default(channel)] : of(to, channel);
        var start = of(from, channel);
        if (to is null || (bool?)from["hold"] == true)
            return start;
        var t = MoviePackage.Ease((string?)from["ease"], (frame - (int)from["frame"]!) / (double)((int)to["frame"]! - (int)from["frame"]!));
        return [.. start.Zip(of(to, channel), (a, b) => a + (b - a) * t)];
    }

    /// <summary>
    /// The whole key of <paramref name="layer"/> on <paramref name="frame"/>: it holds every channel the layer's keys
    /// name, the ones it did not have yet with the values they have on that frame, so making it changes nothing by
    /// itself. On a copy of the layer, it reads the layer's pose on that frame.
    /// </summary>
    public static JsonObject KeyAt(JsonObject layer, int frame)
    {
        var keys = sorted(layer);
        var created = BareKey(layer, frame);
        // A key already there keeps what it holds and gains the rest.
        foreach (var channel in Channels.Where(channel => !Has(created, channel) && keys.Any(key => Has(key, channel))))
        {
            var value = sample(keys, channel, frame);
            if (channel == "tint") SetTint(created, value[0], value[1], value[2]);
            else created[channel] = Math.Round(value[0], 3);
        }
        return created;
    }

    /// <summary>The key on a frame, made empty when there is none: it changes nothing until a channel is set in it.</summary>
    public static JsonObject BareKey(JsonObject layer, int frame)
    {
        var keys = sorted(layer);
        if (keys.FirstOrDefault(key => (int)key["frame"]! == frame) is { } existing)
            return existing;
        var created = new JsonObject { ["frame"] = frame };
        layer["keys"]!.AsArray().Insert(keys.Count(key => (int)key["frame"]! < frame), created);
        return created;
    }

    /// <summary>
    /// Takes a channel out of the key on a frame: the channel then runs past it, between the keys around. A key left
    /// holding nothing goes too, unless it is the layer's first (the layer starts there).
    /// </summary>
    public static void RemoveChannel(JsonObject layer, int frame, string channel)
    {
        var keys = sorted(layer);
        if (keys.FirstOrDefault(key => (int)key["frame"]! == frame) is not { } key)
            return;
        if (channel is "scaleX" or "scaleY" && key["scale"] is { } both)
        {
            var uniform = (double)both;
            key.Remove("scale");
            key[channel == "scaleX" ? "scaleY" : "scaleX"] = uniform;
        }
        key.Remove(channel);
        if (!Channels.Any(each => Has(key, each)) && !ReferenceEquals(key, keys[0]))
            layer["keys"]!.AsArray().Remove(key);
    }

    /// <summary>
    /// A format 1 movie in format 2's terms, the same on every frame: where format 1 left a channel out of a key it
    /// meant the default, so every key gets every channel its layer names anywhere. True when it changed something.
    /// </summary>
    public static bool Upgrade(JsonObject root)
    {
        if ((int?)root["format"] != 1)
            return false;
        foreach (var sprite in root["sprites"] as JsonObject ?? [])
            foreach (var layer in (sprite.Value?["layers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var keys = (layer["keys"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
                foreach (var key in keys)
                    if (key["scale"] is { } both)
                    {
                        var uniform = (double)both;
                        key.Remove("scale");
                        key["scaleX"] ??= uniform;
                        key["scaleY"] ??= uniform;
                    }
                foreach (var channel in Channels.Where(channel => keys.Any(key => key.ContainsKey(channel))))
                    foreach (var key in keys.Where(key => !key.ContainsKey(channel)))
                        key[channel] = channel == "tint" ? new JsonArray(1, 1, 1) : Default(channel);
            }
        root["format"] = 2;
        return true;
    }

    /// <summary>A key's tint (what the clip's colours are multiplied by; 1, 1, 1 leaves them).</summary>
    public static (double Red, double Green, double Blue) Tint(JsonObject key) =>
        key["tint"] is JsonArray { Count: 3 } tint ? ((double)tint[0]!, (double)tint[1]!, (double)tint[2]!) : (1, 1, 1);

    public static void SetTint(JsonObject key, double red, double green, double blue)
    {
        key["tint"] = new JsonArray(Math.Round(red, 3), Math.Round(green, 3), Math.Round(blue, 3)); // white too: a key says what it holds
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
