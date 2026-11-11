using System.Text.Json.Nodes;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Package;

/// <summary>
/// Any Lumen movie described in the shape of a package's movie.json (sprites as layers with keys, labels, script
/// markers, shapes, textures), for looking at how it is built in the editor. In memory only: this is a reading
/// aid for the user's own game data, never written to a file and never a source for a package.
/// The description is approximate where the package format is simpler: a shape shows its first quad, skew is
/// dropped, colour add is left out, scripts are markers ("action 12") without their code.
/// </summary>
public static class MovieReference
{
    /// <param name="movie">The movie.</param>
    /// <param name="textureSizes">Its textures' sizes, by index (shown beside their names).</param>
    /// <returns>The description, and each character's name in it by id.</returns>
    public static (JsonObject Document, IReadOnlyDictionary<uint, string> Names) Describe(LmbMovieDefinition movie,
        IReadOnlyList<(int Width, int Height)> textureSizes)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(textureSizes);
        string text(uint index) => index < movie.Strings.Length ? movie.Strings[(int)index].Value : "";
        static string tidy(string value) => new([.. value.Select(static c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);

        // A sprite is called by its id plus its export name, else the first instance name it is placed under.
        var hints = new Dictionary<uint, string>();
        foreach (var sprite in movie.Sprites)
            if (sprite.ExportStringIndex is { } export && text(export) is { Length: > 0 } exported)
                hints[sprite.CharacterId] = exported.Split('.')[^1];
        foreach (var place in movie.Sprites.SelectMany(static sprite => sprite.Timeline).OfType<LmbPlaceObjectCommand>())
            if (place.NameStringIndex != 0 && text(place.NameStringIndex) is { Length: > 0 } instance)
                hints.TryAdd(place.CharacterId, instance);
        var names = new Dictionary<uint, string>();
        string named(string kind, uint id) => names[id] = hints.TryGetValue(id, out var hint) ? $"{kind}_{id}_{tidy(hint)}" : $"{kind}_{id}";
        foreach (var shape in movie.Shapes)
            named("shape", shape.CharacterId);
        foreach (var field in movie.Texts)
            named("text", field.CharacterId);
        foreach (var sprite in movie.Sprites)
            named("sprite", sprite.CharacterId);

        var textures = new JsonObject();
        for (var index = 0; index < textureSizes.Count; index++)
            textures[$"texture_{index}"] = $"{textureSizes[index].Width} x {textureSizes[index].Height}";

        var shapes = new JsonObject();
        foreach (var shape in movie.Shapes)
        {
            var entry = new JsonObject();
            if (shape.Geometry.FirstOrDefault(static geometry => geometry.Vertices.Length > 0) is { } quad)
            {
                float left = quad.Vertices.Min(static v => v.X), top = quad.Vertices.Min(static v => v.Y);
                entry["rect"] = numbers(left, top, quad.Vertices.Max(static v => v.X) - left, quad.Vertices.Max(static v => v.Y) - top);
                if (quad.Flags >> 16 != 0)
                {
                    entry["texture"] = $"texture_{quad.TextureIndex}";
                    entry["uv"] = numbers(quad.Vertices.Min(static v => v.U), quad.Vertices.Min(static v => v.V),
                        quad.Vertices.Max(static v => v.U), quad.Vertices.Max(static v => v.V));
                }
            }
            if (shape.Geometry.Length > 1)
                entry["quads"] = shape.Geometry.Length;
            shapes[names[shape.CharacterId]] = entry;
        }

        var texts = new JsonObject();
        foreach (var field in movie.Texts)
        {
            var entry = new JsonObject { ["size"] = Math.Round(field.FontSize, 2) };
            if (field.BoundsIndex < movie.Bounds.Length)
            {
                var box = movie.Bounds[(int)field.BoundsIndex];
                entry["rect"] = numbers(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
            }
            texts[names[field.CharacterId]] = entry;
        }

        var sprites = new JsonObject();
        foreach (var sprite in movie.Sprites)
            sprites[names[sprite.CharacterId]] = describe(sprite, movie, names, text);

        var document = new JsonObject
        {
            ["format"] = 1,
            ["frameRate"] = Math.Round(movie.Properties?.FrameRate ?? 60, 2),
            ["root"] = movie.Properties?.RootCharacterId is { } root && names.TryGetValue(root, out var rootName) ? rootName : "",
            ["textures"] = textures,
            ["shapes"] = shapes,
            ["texts"] = texts,
            ["sprites"] = sprites,
        };
        return (document, names);
    }

    private static JsonArray numbers(params float[] values) => new([.. values.Select(static value => (JsonNode)Math.Round(value, 3))]);

    private readonly record struct State(float X, float Y, float ScaleX, float ScaleY, float Rotation, float Alpha, float Red, float Green, float Blue)
    {
        public static State Start => new(0, 0, 1, 1, 0, 1, 1, 1, 1);

        public float[] Values => [X, Y, ScaleX, ScaleY, Rotation, Alpha, Red, Green, Blue];
    }

    private sealed class Layer(uint character, int depth, State state)
    {
        public uint Character = character;
        public int Depth = depth;
        public string Name = "";
        public int Blend;
        public int? Until;
        public State State = state;
        public List<(int Frame, State State)> Keys = [];
    }

    private static JsonObject describe(LmbSpriteDefinition sprite, LmbMovieDefinition movie, Dictionary<uint, string> names, Func<uint, string> text)
    {
        var frames = Math.Max(1, (int)sprite.DeclaredFrameCount);
        var labels = new JsonObject();
        var scripts = new SortedDictionary<int, List<uint>>();
        var layers = new List<Layer>();
        var live = new Dictionary<int, Layer>();
        int? frame = null;
        foreach (var command in sprite.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show:
                    frame = (int)show.Frame;
                    break;
                case LmbFrameKeyCommand:
                    frame = null; // A seek snapshot: the same display list again, not part of the playing timeline.
                    break;
                case LmbFrameLabelCommand label when text(label.StringIndex) is { Length: > 0 } labelName:
                    labels[labelName] = (int)label.Frame;
                    break;
                case LmbDoActionCommand action when frame is { } at:
                    (scripts.TryGetValue(at, out var list) ? list : scripts[at] = []).Add(action.ActionIndex);
                    break;
                case LmbRemoveObjectCommand remove when frame is { } at:
                    if (live.Remove(remove.Depth, out var removed))
                        removed.Until = at;
                    break;
                case LmbPlaceObjectCommand place when frame is { } at:
                    {
                        live.TryGetValue(place.Depth, out var layer);
                        if (place.Mode == 1 || (place.Mode == 3 && layer is not null && layer.Character != place.CharacterId))
                        {
                            var inherited = place.Mode == 3 ? layer!.State : State.Start;
                            if (layer is not null)
                                layer.Until = at;
                            layers.Add(layer = live[place.Depth] = new Layer(place.CharacterId, place.Depth, inherited));
                        }
                        if (layer is null)
                            break;
                        var state = layer.State;
                        if (place.PositionKind == 0 && place.PositionIndex < movie.Matrices.Length)
                        {
                            var m = movie.Matrices[place.PositionIndex];
                            var scaleX = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
                            state = state with
                            {
                                X = m.X, Y = m.Y, ScaleX = scaleX,
                                ScaleY = scaleX > 1e-6f ? (m.M11 * m.M22 - m.M12 * m.M21) / scaleX : MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22),
                                Rotation = MathF.Atan2(m.M12, m.M11) * 180 / MathF.PI,
                            };
                        }
                        else if (place.PositionKind == 0x8000 && place.PositionIndex < movie.Translations.Length)
                        {
                            var t = movie.Translations[place.PositionIndex];
                            state = state with { X = t.X, Y = t.Y, ScaleX = 1, ScaleY = 1, Rotation = 0 };
                        }
                        if (place.ColorMultiplyIndex < movie.ColorTransforms.Length)
                        {
                            var c = movie.ColorTransforms[(int)place.ColorMultiplyIndex];
                            state = state with { Red = c.Red / 256f, Green = c.Green / 256f, Blue = c.Blue / 256f, Alpha = c.Alpha / 256f };
                        }
                        if (place.NameStringIndex != 0 && text(place.NameStringIndex) is { Length: > 0 } instance)
                            layer.Name = instance;
                        if (place.BlendMode != 0)
                            layer.Blend = place.BlendMode;
                        layer.State = state;
                        if (layer.Keys.Count > 0 && layer.Keys[^1].Frame == at)
                            layer.Keys[^1] = (at, state);
                        else
                            layer.Keys.Add((at, state));
                        break;
                    }
            }
        }

        var result = new JsonObject { ["frames"] = frames };
        if (sprite.ExportStringIndex is { } export && text(export) is { Length: > 0 } exported)
            result["export"] = exported;
        if (labels.Count > 0)
            result["labels"] = labels;
        if (scripts.Count > 0)
            result["scripts"] = new JsonObject(scripts.Select(static pair =>
                KeyValuePair.Create(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), (JsonNode?)$"action {string.Join(", ", pair.Value)}")));
        var list2 = new JsonArray();
        foreach (var layer in layers.Where(static layer => layer.Keys.Count > 0))
        {
            var entry = new JsonObject { ["character"] = names.GetValueOrDefault(layer.Character, $"missing_{layer.Character}") };
            if (layer.Name.Length > 0)
                entry["name"] = layer.Name;
            entry["depth"] = layer.Depth;
            if (layer.Blend is not (0 or 1))
                entry["blend"] = layer.Blend switch { 8 => "add", 3 => "multiply", 4 => "screen", var other => $"mode {other}" };
            if (layer.Until is { } until)
                entry["until"] = until;
            entry["keys"] = keys(layer.Keys);
            list2.Add((JsonNode)entry);
        }
        result["layers"] = list2;
        return result;
    }

    // Lumen stores a tween as a placement on every frame and holds values between placements. Here a run of
    // every-frame placements on one straight line becomes its two end keys, and a key held to the next is marked.
    private static JsonArray keys(List<(int Frame, State State)> all)
    {
        var result = new JsonArray();
        for (var start = 0; start < all.Count;)
        {
            var end = start;
            while (end + 1 < all.Count && all[end + 1].Frame == all[end].Frame + 1 && straight(all, start, end + 1))
                end++;
            var (frame, state) = all[start];
            var key = new JsonObject { ["frame"] = frame };
            void set(string name, float value, float fallback)
            {
                if (MathF.Abs(value - fallback) > 1e-4f)
                    key[name] = Math.Round(value, 3);
            }
            set("x", state.X, 0);
            set("y", state.Y, 0);
            set("scaleX", state.ScaleX, 1);
            set("scaleY", state.ScaleY, 1);
            set("rotation", state.Rotation, 0);
            set("alpha", state.Alpha, 1);
            if (state is not { Red: 1, Green: 1, Blue: 1 })
                key["tint"] = numbers(state.Red, state.Green, state.Blue);
            var next = end > start ? end : start + 1;
            if (end == start && next < all.Count && all[next].Frame > frame + 1 && all[next].State != state)
                key["hold"] = true;
            result.Add((JsonNode)key);
            start = next;
        }
        return result;
    }

    // Whether every key strictly between two is where a straight line between them puts it.
    private static bool straight(List<(int Frame, State State)> all, int from, int to)
    {
        float[] a = all[from].State.Values, b = all[to].State.Values;
        for (var index = from + 1; index < to; index++)
        {
            var t = (index - from) / (float)(to - from);
            var values = all[index].State.Values;
            for (var each = 0; each < values.Length; each++)
                if (MathF.Abs(a[each] + (b[each] - a[each]) * t - values[each]) > (each < 2 || each == 4 ? .06f : .004f))
                    return false;
        }
        return true;
    }
}
