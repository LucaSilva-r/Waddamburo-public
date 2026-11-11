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
    private sealed record Snapshot(string Movie, string Metadata);
    private readonly List<Snapshot> _undo = [], _redo = [];
    private string _text;
    private string _metadataText = "{}";
    private JsonObject _metadata = new();

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
        var metadataPath = System.IO.Path.Combine(packageDirectory, MetadataFileName);
        _metadata = File.Exists(metadataPath) ? parse(File.ReadAllText(metadataPath)) : new JsonObject();
        syncAssetIds();
        _metadataText = Format(_metadata);
    }

    public string Path { get; }

    /// <summary>The movie as a tree; change it only inside <see cref="Edit"/>.</summary>
    public JsonObject Root { get; private set; }

    internal long Revision { get; private set; }

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
        var before = new Snapshot(_text, _metadataText);
        try
        {
            change(Root);
            syncAssetIds();
        }
        catch
        {
            Root = parse(before.Movie);
            _metadata = parse(before.Metadata);
            throw;
        }
        var after = Format(Root);
        var metadata = Format(_metadata);
        if (after == normalised(before.Movie) && metadata == before.Metadata)
            return;
        if (!continues || _undo.Count == 0)
            _undo.Add(before);
        _redo.Clear();
        write(new Snapshot(after, metadata));
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

    private void step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0 || ReadOnly)
            return;
        to.Add(new Snapshot(_text, _metadataText));
        var text = from[^1];
        from.RemoveAt(from.Count - 1);
        Root = parse(text.Movie);
        _metadata = parse(text.Metadata);
        write(text);
    }

    private void write(Snapshot text)
    {
        _text = text.Movie;
        _metadataText = text.Metadata;
        File.WriteAllText(Path, _text + (_text.EndsWith('\n') ? "" : "\n"));
        File.WriteAllText(System.IO.Path.Combine(Directory, MetadataFileName), _metadataText + "\n");
        Revision++;
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

}
