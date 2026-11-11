using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;

namespace Waddamburo.Editor;

/// <summary>
/// A movie package's movie.json as the editor changes it (docs/development/free-movies-plan.md). Every edit is
/// written to the file at once, which is also what makes the movie on stage reload; undo and redo are earlier
/// and later texts of the file. ponytail: a file the editor has written loses the comments it had (the JSON
/// tree does not keep them) and is formatted the editor's way.
/// </summary>
public sealed partial class MovieDocument
{
    private static readonly JsonSerializerOptions ValueOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly List<string> _undo = [], _redo = [];
    private string _text;

    private readonly IReadOnlyDictionary<uint, string>? _names;

    /// <summary>
    /// A movie to look at, not to change (the game's own: see MovieReference): every edit is ignored and
    /// nothing is ever written.
    /// </summary>
    public MovieDocument(JsonObject description, IReadOnlyDictionary<uint, string> names)
    {
        Path = "";
        _text = "";
        Root = description;
        _names = names;
    }

    /// <summary>Reference only: edits are ignored.</summary>
    public bool ReadOnly => _names is not null;

    public MovieDocument(string packageDirectory)
    {
        Path = System.IO.Path.Combine(packageDirectory, MoviePackage.FileName);
        _text = File.ReadAllText(Path);
        Root = parse(_text);
    }

    public string Path { get; }

    /// <summary>The movie as a tree; change it only inside <see cref="Edit"/>.</summary>
    public JsonObject Root { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after the file was written: the movie on stage should load it.</summary>
    public event Action? Changed;

    private static JsonObject parse(string text) =>
        JsonNode.Parse(text, documentOptions: MoviePackage.ReadOptions) as JsonObject ?? throw new FormatException("movie.json is not an object.");

    /// <summary>
    /// Applies a change and writes the file. <paramref name="continues"/>: the same gesture as the last edit (a
    /// drag), so one undo takes the whole gesture back.
    /// </summary>
    public void Edit(Action<JsonObject> change, bool continues = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (ReadOnly)
            return;
        var before = _text;
        change(Root);
        var after = Format(Root);
        if (after == normalised(before))
            return;
        if (!continues || _undo.Count == 0)
            _undo.Add(before);
        _redo.Clear();
        write(after);
    }

    private static string normalised(string text) => Format(parse(text));

    /// <summary>
    /// The file's layout: a line per entry, with anything small (a key, a rect, a text field) on one line, so the
    /// file reads like a hand-written one and a change is a short diff.
    /// </summary>
    public static string Format(JsonNode? node)
    {
        var text = new System.Text.StringBuilder();
        write(text, node, 0);
        return text.ToString();

        static bool flat(JsonNode? value) => value is null or JsonValue
            || (value is JsonArray array && array.All(static each => each is null or JsonValue));
        static string inline(JsonNode? value) => value switch
        {
            null => "null",
            JsonArray array => $"[{string.Join(", ", array.Select(inline))}]",
            JsonObject entries => entries.Count == 0 ? "{}"
                : $"{{ {string.Join(", ", entries.Select(static entry => $"{name(entry.Key)}: {inline(entry.Value)}"))} }}",
            _ => value.ToJsonString(ValueOptions),
        };
        static string name(string key) => JsonValue.Create(key).ToJsonString(ValueOptions);
        static void write(System.Text.StringBuilder text, JsonNode? value, int level)
        {
            var small = flat(value) || (value is JsonObject leaf && leaf.All(static entry => flat(entry.Value)));
            if (small && inline(value) is { Length: <= 110 } line)
            {
                text.Append(line);
                return;
            }
            var indent = new string(' ', 2 * level + 2);
            var entries = value is JsonObject map ? map.Select(entry => (Key: (string?)entry.Key, entry.Value)).ToList()
                : ((JsonArray)value!).Select(static each => (Key: (string?)null, Value: each)).ToList();
            text.Append(value is JsonObject ? '{' : '[').Append('\n');
            for (var index = 0; index < entries.Count; index++)
            {
                text.Append(indent);
                if (entries[index].Key is { } key)
                    text.Append(name(key)).Append(": ");
                write(text, entries[index].Value, level + 1);
                text.Append(index + 1 < entries.Count ? ",\n" : "\n");
            }
            text.Append(' ', 2 * level).Append(value is JsonObject ? '}' : ']');
        }
    }

    public void Undo() => step(_undo, _redo);

    public void Redo() => step(_redo, _undo);

    private void step(List<string> from, List<string> to)
    {
        if (from.Count == 0 || ReadOnly)
            return;
        to.Add(_text);
        var text = from[^1];
        from.RemoveAt(from.Count - 1);
        Root = parse(text);
        write(text);
    }

    private void write(string text)
    {
        _text = text;
        File.WriteAllText(Path, text + (text.EndsWith('\n') ? "" : "\n"));
        Changed?.Invoke();
    }

    /// <summary>A character's name by its id in the assembled movie (see <see cref="MoviePackage.CharacterNames"/>).</summary>
    public string? CharacterName(uint id)
    {
        if (_names is not null)
            return _names.GetValueOrDefault(id);
        using var document = JsonDocument.Parse(_text, MoviePackage.ReadOptions);
        var names = MoviePackage.CharacterNames(document.RootElement);
        return id >= 1 && id <= names.Count ? names[(int)id - 1] : null;
    }

    /// <summary>A character's id in the assembled movie by its name.</summary>
    public uint? CharacterId(string name)
    {
        if (_names is not null)
            return _names.FirstOrDefault(pair => pair.Value == name) is { Value: not null } found ? found.Key : null;
        string[] all = [.. CharacterGroups.SelectMany(each => group(each).Select(static entry => entry.Key))];
        return Array.IndexOf(all, name) is >= 0 and var index ? (uint)index + 1 : null;
    }

    /// <summary>The layer of sprite <paramref name="sprite"/> at timeline depth <paramref name="depth"/> placing <paramref name="character"/>.</summary>
    public JsonObject? Layer(string sprite, long depth, string character)
    {
        if (Root["sprites"]?[sprite]?["layers"] is not JsonArray layers)
            return null;
        for (var index = 0; index < layers.Count; index++)
            if (layers[index] is JsonObject layer && (string?)layer["character"] == character
                && ((double?)layer["depth"] ?? index + 1) == depth)
                return layer;
        return null;
    }

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
        (double?)key[property] ?? (property is "scaleX" or "scaleY" ? (double?)key["scale"] : null) ?? Default(property);

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
