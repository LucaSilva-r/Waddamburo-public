using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Editor;

/// <summary>
/// A skin's scenes.json being edited (<see cref="SkinScenes"/> is the format): layers added, moved, reordered, removed,
/// with undo as whole-text snapshots and an explicit save. A screen the file leaves out is shown from the engine's
/// built-in layers (<see cref="BuiltIn"/>), and editing it writes it into the file.
/// </summary>
public sealed class SceneDocument
{
    private const string Empty = "{ \"format\": 1, \"scenes\": {} }";
    private readonly List<string> _undo = [], _redo = [];
    private string _saved;
    private int _revision, _continuedRevision = -1;

    public SceneDocument(string skinRoot, Func<string, IReadOnlyList<SkinScenes.Layer>>? builtIn = null)
    {
        Root = skinRoot;
        BuiltIn = builtIn ?? (static _ => []);
        Text = _saved = File.Exists(FilePath) ? File.ReadAllText(FilePath) : Empty;
        parse();
    }

    public string Root { get; }

    public string FilePath => System.IO.Path.Combine(Root, SkinScenes.FileName);

    /// <summary>The engine's layers for a screen the file leaves out.</summary>
    public Func<string, IReadOnlyList<SkinScenes.Layer>> BuiltIn { get; }

    public string Text { get; private set; }

    /// <summary>The file as the engine reads it; null while <see cref="Problem"/> says why it does not read.</summary>
    public SkinScenes? Parsed { get; private set; }

    public string? Problem { get; private set; }

    public bool IsDirty => Text != _saved;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every edit, undo and redo.</summary>
    public event Action? Changed;

    public event Action? Saved;

    /// <summary>Whether the file composes this screen itself (false: the engine's built-in layers).</summary>
    public bool Composes(string key) => Parsed?.Layers(key) is not null;

    public IReadOnlyList<SkinScenes.Layer> Layers(string key) => Parsed?.Layers(key) ?? BuiltIn(key);

    /// <summary>
    /// Changes the screen's layer list (a copy of what is shown, written back whole). <paramref name="continues"/>:
    /// part of the same gesture as the last edit (a drag), one undo step. A result the engine would refuse is undone
    /// and thrown as <see cref="InvalidOperationException"/> with the reason.
    /// </summary>
    public void Edit(string key, Action<List<SkinScenes.Layer>> change, bool continues = false)
    {
        var layers = Layers(key).ToList();
        change(layers);
        var root = JsonNode.Parse(Text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        var scenes = root["scenes"] as JsonObject ?? (JsonObject)(root["scenes"] = new JsonObject());
        scenes[key] = new JsonObject { ["layers"] = new JsonArray([.. layers.Select(write)]) };
        var text = format(root);
        try
        {
            SkinScenes.Parse(text);
        }
        catch (InvalidDataException refused)
        {
            throw new InvalidOperationException(refused.Message, refused);
        }
        if (!(continues && _continuedRevision == _revision))
            _undo.Add(Text);
        _redo.Clear();
        Text = text;
        _continuedRevision = ++_revision;
        parse();
        Changed?.Invoke();
    }

    public void Undo() => step(_undo, _redo);

    public void Redo() => step(_redo, _undo);

    private void step(List<string> from, List<string> to)
    {
        if (from.Count == 0)
            return;
        to.Add(Text);
        Text = from[^1];
        from.RemoveAt(from.Count - 1);
        _revision++;
        parse();
        Changed?.Invoke();
    }

    public void Save()
    {
        File.WriteAllText(FilePath, Text);
        _saved = Text;
        Saved?.Invoke();
    }

    private void parse()
    {
        try
        {
            Parsed = SkinScenes.Parse(Text);
            Problem = null;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            Parsed = null;
            Problem = error.Message;
        }
    }

    private static JsonObject write(SkinScenes.Layer layer)
    {
        var node = new JsonObject { ["movie"] = layer.Movie, ["role"] = layer.Role };
        if (layer.X != 0) node["x"] = Math.Round(layer.X, 2);
        if (layer.Y != 0) node["y"] = Math.Round(layer.Y, 2);
        if (layer.Side is { } side) node["side"] = side;
        if (layer.ScaleX != 1) node["scaleX"] = Math.Round(layer.ScaleX, 4);
        if (layer.ScaleY != 1) node["scaleY"] = Math.Round(layer.ScaleY, 4);
        if (layer.Rotation != 0) node["rotation"] = Math.Round(layer.Rotation, 2);
        return node;
    }

    // One screen per line block and one layer per line: hand-edited files stay readable and diffs short.
    private static string format(JsonObject root)
    {
        var lines = new List<string> { "{", $"  \"format\": {root["format"]?.ToJsonString() ?? "1"},", "  \"scenes\": {" };
        var scenes = root["scenes"]!.AsObject().ToList();
        for (var index = 0; index < scenes.Count; index++)
        {
            var (key, scene) = scenes[index];
            var layers = scene?["layers"]?.AsArray() ?? [];
            var comma = index < scenes.Count - 1 ? "," : "";
            if (layers.Count <= 1)
            {
                lines.Add($"    {JsonSerializer.Serialize(key)}: {{ \"layers\": [{(layers.Count == 1 ? $" {compact(layers[0]!)} " : "")}] }}{comma}");
                continue;
            }
            lines.Add($"    {JsonSerializer.Serialize(key)}: {{ \"layers\": [");
            lines.AddRange(layers.Select((layer, at) => $"      {compact(layer!)}{(at < layers.Count - 1 ? "," : "")}"));
            lines.Add($"    ] }}{comma}");
        }
        lines.Add("  }");
        lines.Add("}");
        return string.Join('\n', lines) + "\n";
    }

    private static string compact(JsonNode layer) =>
        "{ " + string.Join(", ", layer.AsObject().Select(static pair => $"{JsonSerializer.Serialize(pair.Key)}: {value(pair.Value)}")) + " }";

    private static string value(JsonNode? node) => node is JsonValue number && number.TryGetValue<double>(out var parsed)
        ? parsed.ToString("0.####", CultureInfo.InvariantCulture) : node?.ToJsonString() ?? "null";
}
