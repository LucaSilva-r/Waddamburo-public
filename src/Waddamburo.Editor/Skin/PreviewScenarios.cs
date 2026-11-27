using System.Globalization;
using System.Text.Json;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// A named set of example data for a screen's previews, on top of the editor's own (<see cref="PreviewData"/>): engine
/// states, list places and timed events, as the provider gives them to movies. Kept in the skin, one file per screen:
/// <c>previews/&lt;screen&gt;.json</c>.
/// <code>
/// { "format": 1, "scenarios": [ { "name": "Full combo",
///     "state": { "result.fullCombo": true, "result.bad": 0, "player.name@2": "Wada-don" },
///     "lists": { "scores.name": ["A", "B"], "scores.score": [1000000, 990000] },
///     "events": [ { "at": 1.5, "name": "note.judged", "args": ["good"] } ], "loop": 4 } ] }
/// </code>
/// "@2" after a name: that drum's (1 left, 2 right). A list sets its ".count" state too when the provider has one.
/// Events repeat every <c>loop</c> seconds (default: a second after the last one).
/// </summary>
public sealed record PreviewScenario(
    string Name,
    IReadOnlyList<(string Name, int Drum, LumenHostValue Value)> States,
    IReadOnlyList<(string Name, int Drum, int Index, LumenHostValue Value)> Items,
    IReadOnlyList<(double At, string Name, int Drum, LumenHostValue[] Arguments)> Events,
    double Loop)
{
    public const string Folder = "previews";

    public static string FilePath(string skinRoot, string screen) => Path.Combine(skinRoot, Folder, screen + ".json");

    /// <summary>A screen's scenarios, and what in its file is wrong (each named; a wrong entry is left out, the rest kept).</summary>
    public static (IReadOnlyList<PreviewScenario> Scenarios, IReadOnlyList<string> Problems) Load(string skinRoot, string screen)
    {
        var path = FilePath(skinRoot, screen);
        if (!File.Exists(path))
            return ([], []);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            return ([], [$"{Path.GetFileName(path)}: {error.Message}"]);
        }
    }

    public static (IReadOnlyList<PreviewScenario> Scenarios, IReadOnlyList<string> Problems) Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != 1)
            throw new InvalidDataException("\"format\": 1 expected.");
        if (!root.TryGetProperty("scenarios", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("\"scenarios\" array expected.");
        var scenarios = new List<PreviewScenario>();
        var problems = new List<string>();
        foreach (var entry in list.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString()! : $"Scenario {scenarios.Count + 1}";
            void problem(string text) => problems.Add($"{name}: {text}");
            (string Name, int Drum)? key(string text, WdKind kind)
            {
                var parts = text.Split('@', 2);
                var drum = 0;
                if (parts.Length == 2 && !(int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out drum) && drum is 1 or 2))
                {
                    problem($"\"{text}\": a drum is @1 (left) or @2 (right).");
                    return null;
                }
                if (WdNames.Find(parts[0]) is not { } known)
                {
                    problem($"\"{parts[0]}\" is no name the engine gives (WdNames).");
                    return null;
                }
                if (known.Kind != kind)
                {
                    problem($"\"{parts[0]}\" is a {known.Kind.ToString().ToLowerInvariant()}, not a {kind.ToString().ToLowerInvariant()}.");
                    return null;
                }
                return (parts[0], drum);
            }
            var states = new List<(string, int, LumenHostValue)>();
            if (entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                foreach (var pair in state.EnumerateObject())
                    if (key(pair.Name, WdKind.State) is { } at)
                        states.Add((at.Name, at.Drum, value(pair.Value)));
            var items = new List<(string, int, int, LumenHostValue)>();
            if (entry.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Object)
                foreach (var pair in lists.EnumerateObject())
                {
                    if (key(pair.Name, WdKind.List) is not { } at)
                        continue;
                    if (pair.Value.ValueKind != JsonValueKind.Array)
                    {
                        problem($"\"{pair.Name}\": a list is an array.");
                        continue;
                    }
                    var places = pair.Value.EnumerateArray().Select(value).ToArray();
                    for (var index = 0; index < places.Length; index++)
                        items.Add((at.Name, at.Drum, index, places[index]));
                    // scores.name -> scores.count, when the provider has it and the state does not set it.
                    var count = at.Name[..at.Name.LastIndexOf('.')] + ".count";
                    if (WdNames.Find(count) is { Kind: WdKind.State } && !states.Any(each => each.Item1 == count && each.Item2 == at.Drum))
                        states.Add((count, at.Drum, LumenHostValue.FromNumber(places.Length)));
                }
            var events = new List<(double, string, int, LumenHostValue[])>();
            if (entry.TryGetProperty("events", out var timed) && timed.ValueKind == JsonValueKind.Array)
                foreach (var each in timed.EnumerateArray())
                {
                    var at = each.TryGetProperty("at", out var when) && when.ValueKind == JsonValueKind.Number ? when.GetDouble() : 0;
                    if (!each.TryGetProperty("name", out var eventName) || eventName.ValueKind != JsonValueKind.String
                        || key(eventName.GetString()!, WdKind.Event) is not { } named2)
                    {
                        if (!each.TryGetProperty("name", out _)) problem("an event needs a \"name\".");
                        continue;
                    }
                    var arguments = each.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                        ? args.EnumerateArray().Select(value).ToArray() : [];
                    events.Add((Math.Max(0, at), named2.Name, named2.Drum, arguments));
                }
            var loop = entry.TryGetProperty("loop", out var looped) && looped.ValueKind == JsonValueKind.Number && looped.GetDouble() > 0
                ? looped.GetDouble() : events.Count == 0 ? 0 : events.Max(static each => each.Item1) + 1;
            scenarios.Add(new(name, states, items, events, loop));
        }
        return (scenarios, problems);
    }

    private static LumenHostValue value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => LumenHostValue.FromNumber(element.GetDouble()),
        JsonValueKind.String => LumenHostValue.FromString(element.GetString()!),
        JsonValueKind.True => LumenHostValue.FromBoolean(true),
        JsonValueKind.False => LumenHostValue.FromBoolean(false),
        _ => LumenHostValue.Undefined,
    };

    /// <summary>Gives a movie this scenario's states and lists (over the example data it already has).</summary>
    public void Apply(LumenPlayer player)
    {
        foreach (var (name, drum, value) in States)
            player.Publish(name, value, drum);
        foreach (var (name, drum, index, value) in Items)
            player.PublishItem(name, index, value, drum);
    }

    /// <summary>The events due between two times of the scenario's clock (seconds), the loop wrapped.</summary>
    public IEnumerable<(string Name, int Drum, LumenHostValue[] Arguments)> Due(double from, double to)
    {
        if (Events.Count == 0 || Loop <= 0 || to <= from)
            yield break;
        var (a, b) = (from % Loop, to % Loop);
        foreach (var (at, name, drum, arguments) in Events)
            if (a <= b ? at >= a && at < b : at >= a || at < b)
                yield return (name, drum, arguments);
    }

    /// <summary>A new scenario for the screen's file: its name, and the states and lists the screen's movies read (as an example to change).</summary>
    public static string Skeleton(string name) => $$"""
        { "name": {{JsonSerializer.Serialize(name)}},
              "state": { "player.name@1": "Don-chan" },
              "lists": {},
              "events": [] }
        """;
}
