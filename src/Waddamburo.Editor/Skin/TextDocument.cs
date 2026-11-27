using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Editor;

/// <summary>
/// A skin's text.json being edited (its fonts and text styles): changed in place through <see cref="Edit"/>, one undo
/// step per gesture, checked as the engine reads it on every change (<see cref="Parsed"/>, or <see cref="Problem"/>).
/// </summary>
public sealed class TextDocument
{
    private const string Empty = "{\n  \"format\": 1,\n  \"fonts\": {},\n  \"styles\": {}\n}\n";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Stack<string> _undo = new(), _redo = new();
    private string _saved;

    public TextDocument(string root)
    {
        Root = root;
        _saved = File.Exists(FilePath) ? File.ReadAllText(FilePath) : Empty;
        Text = _saved;
        Json = new JsonObject();
        read();
    }

    public string Root { get; }

    public string FilePath => Path.Combine(Root, SkinText.FileName);

    /// <summary>The file's text now (unsaved edits included).</summary>
    public string Text { get; private set; }

    /// <summary>The file as JSON, changed in place by <see cref="Edit"/> (a new object after an undo).</summary>
    public JsonObject Json { get; private set; }

    /// <summary>The fonts and styles as the engine reads them; null while <see cref="Problem"/> says why not.</summary>
    public SkinText? Parsed { get; private set; }

    public string? Problem { get; private set; }

    public bool IsDirty => Text != _saved;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>After every change (edit, undo, redo).</summary>
    public event Action? Changed;

    /// <summary>Changes <see cref="Json"/>; <paramref name="continues"/>: part of the last undo step (a drag going on).</summary>
    public void Edit(Action<JsonObject> change, bool continues = false)
    {
        var before = Text;
        change(Json);
        var after = Json.ToJsonString(Indented) + "\n";
        if (after == before)
            return;
        if (!continues || _undo.Count == 0)
            _undo.Push(before);
        _redo.Clear();
        Text = after;
        check();
    }

    public void Undo() => step(_undo, _redo);

    public void Redo() => step(_redo, _undo);

    public void Save()
    {
        File.WriteAllText(FilePath, Text);
        _saved = Text;
    }

    private void step(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0)
            return;
        to.Push(Text);
        Text = from.Pop();
        read();
    }

    private void read()
    {
        try
        {
            Json = JsonNode.Parse(Text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            Json = JsonNode.Parse(Empty)!.AsObject();
        }
        check();
    }

    private void check()
    {
        try
        {
            Parsed = SkinText.Parse(Text, Root);
            Problem = null;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            Parsed = null;
            Problem = error.Message;
        }
        Changed?.Invoke();
    }
}
