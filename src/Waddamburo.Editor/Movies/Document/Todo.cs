using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Waddamburo.Editor;

public enum MovieTodoState { Missing, Placeholder, Done }

/// <summary>
/// One thing a movie owes the game, and how far it is: a state the game jumps to (a label), a clip it addresses (a
/// named sprite), or a call it makes (a callback). Read from the package itself.
/// </summary>
public sealed record MovieTodo(string Kind, string Sprite, string Name, MovieTodoState State, string Detail, int Frame = 0, string? File = null, int Line = 1);

public sealed partial class MovieDocument
{
    private static readonly Regex Callback = new("""addCallback\(\s*"(\w+)"[^{]*\{(\s*)\}?""", RegexOptions.Compiled);

    /// <summary>
    /// What is left to do in this package: every label with nothing or only placeholder boxes showing in its frames,
    /// every sprite that is only a placeholder box, every callback whose function is empty; the finished ones too.
    /// </summary>
    public IReadOnlyList<MovieTodo> Todo()
    {
        var list = new List<MovieTodo>();
        // A placeholder: the magenta box a skeleton draws where art is still to come.
        bool placeholder(string? character) => character is not null && Root["shapes"]?[character] is JsonObject shape
            && ((string?)shape["texture"] is { } texture && ((string?)Root["textures"]?[texture])?.EndsWith("box.svg", StringComparison.Ordinal) == true);
        MovieTodoState state(IEnumerable<JsonObject> layers)
        {
            var shown = layers.ToArray();
            return shown.Length == 0 ? MovieTodoState.Missing
                : shown.All(layer => placeholder((string?)layer["character"])) ? MovieTodoState.Placeholder : MovieTodoState.Done;
        }
        foreach (var (name, node) in group("sprites"))
        {
            if (node is not JsonObject sprite)
                continue;
            var frames = Math.Max(1, (int?)sprite["frames"] ?? 1);
            var layers = (sprite["layers"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            var labels = (sprite["labels"] as JsonObject ?? []).Select(static label => (label.Key, Frame: (int)label.Value!)).OrderBy(static label => label.Frame).ToArray();
            for (var index = 0; index < labels.Length; index++)
            {
                var (label, from) = labels[index];
                var to = labels.Skip(index + 1).Select(static next => next.Frame).FirstOrDefault(next => next > from, frames);
                // Alive in the label's frames: first key before its end, not removed before its start.
                var found = state(layers.Where(layer => layer["keys"] is JsonArray { Count: > 0 } keys
                    && keys.Min(static key => (int)key!["frame"]!) < to && ((int?)layer["until"] ?? int.MaxValue) > from));
                list.Add(new("state", name, label, found, found switch
                {
                    MovieTodoState.Missing => $"frames {from} to {to - 1}: nothing is shown",
                    MovieTodoState.Placeholder => $"frames {from} to {to - 1}: placeholder boxes only",
                    _ => $"frames {from} to {to - 1}",
                }, from));
            }
            if (labels.Length == 0 && name != (string?)Root["root"] && layers.Length > 0 && state(layers) == MovieTodoState.Placeholder)
                list.Add(new("clip", name, name, MovieTodoState.Placeholder, "a placeholder box: the game addresses this clip by name"));
        }
        foreach (var file in Scripts().Select(static script => script.File).Distinct(StringComparer.Ordinal))
        {
            string text;
            try
            {
                text = System.Text.Encoding.UTF8.GetString(ReadFile(file));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }
            foreach (Match match in Callback.Matches(text))
            {
                var empty = match.Value.EndsWith('}');
                list.Add(new("call", "", match.Groups[1].Value, empty ? MovieTodoState.Missing : MovieTodoState.Done,
                    empty ? "the game calls it; it does nothing yet" : "written", File: file, Line: text[..match.Index].Count(static c => c == '\n') + 1));
            }
        }
        return list;
    }
}
