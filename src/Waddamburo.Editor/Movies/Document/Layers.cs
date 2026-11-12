using System.Text.Json.Nodes;

namespace Waddamburo.Editor;

public sealed partial class MovieDocument
{
    // -------------------------------------------------------------------------------------------- layers

    public static readonly string[] Blends = ["normal", "add", "multiply", "screen"];

    private JsonArray layersOf(string sprite) => group("sprites")[sprite]?["layers"] as JsonArray
        ?? throw new InvalidOperationException($"There is no sprite '{sprite}'.");

    private JsonObject layerAt(string sprite, int index) => index >= 0 && index < layersOf(sprite).Count && layersOf(sprite)[index] is JsonObject layer
        ? layer : throw new InvalidOperationException($"'{sprite}' has no layer {index}.");

    /// <summary>
    /// Moves a layer in its sprite's list: later in the list is drawn on top. The layers' depths become their places
    /// in the list (explicit depths are dropped), so the list is the drawing order.
    /// </summary>
    public void MoveLayer(string sprite, int from, int to)
    {
        var layers = layersOf(sprite);
        layerAt(sprite, from);
        to = Math.Clamp(to, 0, layers.Count - 1);
        Edit(_ =>
        {
            foreach (var each in layers.OfType<JsonObject>())
                each.Remove("depth");
            var layer = layers[from];
            layers.RemoveAt(from);
            layers.Insert(to, layer);
        });
    }

    /// <summary>
    /// Takes a layer out of one sprite and puts it in another's list at <paramref name="to"/> (its keys as they are:
    /// the same numbers in the new parent's space). Refused when the layer's sprite would end up inside itself.
    /// </summary>
    public void MoveLayerTo(string sprite, int from, string target, int to)
    {
        var layers = layersOf(sprite);
        var layer = layerAt(sprite, from);
        var destination = layersOf(target);
        var sprites = Root["sprites"]!.AsObject();
        bool reaches(string name, HashSet<string> seen) => name == target || seen.Add(name) && sprites[name]?["layers"] is JsonArray inner
            && inner.OfType<JsonObject>().Any(each => (string?)each["character"] is { } child && sprites.ContainsKey(child) && reaches(child, seen));
        if ((string?)layer["character"] is { } character && sprites.ContainsKey(character) && reaches(character, []))
            throw new InvalidOperationException($"'{character}' would end up inside itself.");
        Edit(_ =>
        {
            foreach (var each in layers.OfType<JsonObject>().Concat(destination.OfType<JsonObject>()))
                each.Remove("depth");
            layers.RemoveAt(from);
            layer.Remove("until"); // a lifetime counted in the old timeline's frames
            destination.Insert(Math.Clamp(to, 0, destination.Count), layer);
        });
    }

    /// <summary>A copy of a layer, right above it; returns its place in the list.</summary>
    public int DuplicateLayer(string sprite, int index)
    {
        var layers = layersOf(sprite);
        var copy = layerAt(sprite, index).DeepClone();
        Edit(_ =>
        {
            foreach (var each in layers.OfType<JsonObject>())
                each.Remove("depth");
            layers.Insert(index + 1, copy);
        });
        return index + 1;
    }

    public void SetBlend(string sprite, int index, string blend)
    {
        if (!Blends.Contains(blend))
            throw new InvalidOperationException($"There is no blend '{blend}'.");
        var layer = layerAt(sprite, index);
        Edit(_ =>
        {
            if (blend == "normal") layer.Remove("blend"); else layer["blend"] = blend;
        });
    }

    /// <summary>The frame a layer is removed on (after its last key, inside the sprite); null: it stays to the end.</summary>
    /// <summary>The frame a layer appears on; null: on its first key's frame.</summary>
    public void SetFrom(string sprite, int index, int? frame)
    {
        var layer = layerAt(sprite, index);
        var end = Math.Min((int?)layer["until"] ?? int.MaxValue, (int?)group("sprites")[sprite]!["frames"] ?? 1);
        if (frame is { } at && (at < 0 || at >= end))
            throw new InvalidOperationException("A layer appears inside the sprite's frames, before it is removed.");
        Edit(_ =>
        {
            if (frame is null) layer.Remove("from"); else layer["from"] = frame;
        });
    }

    /// <summary>The frame a layer appears on (see <see cref="SetFrom"/>).</summary>
    public static int From(JsonObject layer) => (int?)layer["from"] ?? layer["keys"]!.AsArray().Min(static key => (int)key!["frame"]!);

    public void SetUntil(string sprite, int index, int? frame)
    {
        var layer = layerAt(sprite, index);
        var last = layer["keys"]!.AsArray().Max(static key => (int)key!["frame"]!);
        if (frame is { } at && (at <= last || at <= From(layer) || at >= ((int?)group("sprites")[sprite]!["frames"] ?? 1)))
            throw new InvalidOperationException("A layer ends after its last key and before the sprite's last frame.");
        Edit(_ =>
        {
            if (frame is null) layer.Remove("until"); else layer["until"] = frame;
        });
    }

    /// <summary>
    /// Makes a layer a mask: it is not drawn, and the <paramref name="count"/> layers above it only show inside
    /// its shape. Zero: an ordinary layer again.
    /// </summary>
    public void SetMask(string sprite, int index, int count)
    {
        var layer = layerAt(sprite, index);
        if (count < 0 || index + count > layersOf(sprite).Count - 1)
            throw new InvalidOperationException("A mask covers layers above it in the list.");
        Edit(_ =>
        {
            // Masks count depths: the list has to be the depth order.
            foreach (var each in layersOf(sprite).OfType<JsonObject>())
                each.Remove("depth");
            if (count == 0) layer.Remove("masks"); else layer["masks"] = count;
        });
    }
    /// <summary>The layer of sprite <paramref name="sprite"/> at timeline depth <paramref name="depth"/> placing <paramref name="character"/>.</summary>
    public JsonObject? Layer(string sprite, long depth, string character)
    {
        if (Root["sprites"]?[sprite]?["layers"] is not JsonArray layers)
            return null;
        for (var index = 0; index < layers.Count; index++)
            if (layers[index] is JsonObject layer && (string?)layer["character"] == character
                && ((int?)layer["depth"] ?? index + 1) == depth)
                return layer;
        return null;
    }

}
