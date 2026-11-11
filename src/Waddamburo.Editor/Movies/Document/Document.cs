using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;

namespace Waddamburo.Editor;

/// <summary>
/// A working movie package with explicit saving. Edits and undo/redo update the preview in memory;
/// Save writes JSON, stable identities, and staged artwork/scripts. Saved JSON uses the editor's formatting.
/// </summary>
public sealed partial class MovieDocument
{
    private static readonly JsonSerializerOptions ValueOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Snapshot(string Movie, string Metadata, Dictionary<string, byte[]> Files);
    private readonly List<Snapshot> _undo = [], _redo = [];
    private string _text;
    private string _metadataText = "{}";
    private JsonObject _metadata = new();
    private Dictionary<string, byte[]> _pendingFiles = new(StringComparer.Ordinal);
    private Snapshot? _saved;
    private Snapshot snapshot() => new(_text, _metadataText, new(_pendingFiles, StringComparer.Ordinal));
    public string Text => _text;
    public bool IsDirty => !ReadOnly && _saved is { } saved && (_text != saved.Movie || _metadataText != saved.Metadata
        || _pendingFiles.Count != saved.Files.Count || _pendingFiles.Any(file => !saved.Files.TryGetValue(file.Key, out var bytes) || !sameBytes(file.Value, bytes)));

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
        _saved = snapshot();
    }

    public string Path { get; }

    /// <summary>The movie as a tree; change it only inside <see cref="Edit"/>.</summary>
    public JsonObject Root { get; private set; }

    internal long Revision { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised when the working document changes: the preview should reload from memory.</summary>
    public event Action? Changed;
    /// <summary>Raised after an explicit save, so hosts can acknowledge their own file changes without restarting playback.</summary>
    public event Action? Saved;

    private static JsonObject parse(string text) =>
        JsonNode.Parse(text, documentOptions: MoviePackage.ReadOptions) as JsonObject ?? throw new FormatException("movie.json is not an object.");

    /// <summary>
    /// Applies a change to the working document. <paramref name="continues"/>: the same gesture as the last edit (a
    /// drag), so one undo takes the whole gesture back.
    /// </summary>
    public void Edit(Action<JsonObject> change, bool continues = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (ReadOnly)
            return;
        var before = snapshot();
        try
        {
            change(Root);
            syncAssetIds();
            var referenced = referencedFiles();
            foreach (var file in _pendingFiles.Keys.Where(file => !referenced.Contains(file)).ToArray()) _pendingFiles.Remove(file);
        }
        catch
        {
            Root = parse(before.Movie);
            _metadata = parse(before.Metadata);
            _pendingFiles = new(before.Files, StringComparer.Ordinal);
            throw;
        }
        var after = Format(Root);
        var metadata = Format(_metadata);
        if (after == normalised(before.Movie) && metadata == before.Metadata && sameFiles(before.Files))
            return;
        if (!continues || _undo.Count == 0)
            _undo.Add(before);
        _redo.Clear();
        apply(new Snapshot(after, metadata, new(_pendingFiles, StringComparer.Ordinal)));
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
        to.Add(snapshot());
        var text = from[^1];
        from.RemoveAt(from.Count - 1);
        Root = parse(text.Movie);
        _metadata = parse(text.Metadata);
        apply(text);
    }

    private static bool sameBytes(byte[] first, byte[] second) => ReferenceEquals(first, second) || first.AsSpan().SequenceEqual(second);

    private bool sameFiles(Dictionary<string, byte[]> files) => _pendingFiles.Count == files.Count
        && _pendingFiles.All(file => files.TryGetValue(file.Key, out var bytes) && sameBytes(file.Value, bytes));

    private void apply(Snapshot text)
    {
        _text = text.Movie;
        _metadataText = text.Metadata;
        _pendingFiles = new(text.Files, StringComparer.Ordinal);
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>Reads the current working asset, including artwork and new scripts which have not been saved.</summary>
    public byte[] ReadFile(string relative) => _pendingFiles.TryGetValue(relative, out var bytes)
        && (_saved is null || !_saved.Files.TryGetValue(relative, out var saved) || !sameBytes(bytes, saved))
        ? bytes.ToArray() : File.ReadAllBytes(FilePath(relative));

    private bool hasFile(string relative) => _pendingFiles.ContainsKey(relative) || File.Exists(FilePath(relative));
    private HashSet<string> referencedFiles() => group("textures").Select(entry => (string)entry.Value!)
        .Concat(Scripts().Select(script => script.File)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Writes the working package and companion metadata only when explicitly requested.</summary>
    public void Save()
    {
        if (ReadOnly) throw new InvalidOperationException("Reference movies cannot be saved.");
        var referenced = referencedFiles();
        foreach (var file in _pendingFiles.Where(file => referenced.Contains(file.Key)))
        {
            var target = FilePath(file.Key);
            if (_saved is { } previous && previous.Files.TryGetValue(file.Key, out var saved)
                && sameBytes(saved, file.Value) && File.Exists(target)) continue;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Value);
        }
        File.WriteAllText(Path, _text + (_text.EndsWith('\n') ? "" : "\n"));
        File.WriteAllText(System.IO.Path.Combine(Directory, MetadataFileName), _metadataText + "\n");
        _saved = snapshot();
        Saved?.Invoke();
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
