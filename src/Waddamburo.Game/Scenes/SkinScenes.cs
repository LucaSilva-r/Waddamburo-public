using System.Text.Json;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

/// <summary>
/// A skin's screens (<c>scenes.json</c> at the skin's root): which movies make each screen and where. The engine keeps
/// what a screen needs to work (<see cref="Required"/>: the roles its hosts drive); everything else is the skin's.
/// <code>
/// { "format": 1, "scenes": { "entry": { "layers": [ { "movie": "entry/entry", "role": "player-entry" },
///   { "movie": "indicator/player_name", "role": "indicator-part", "x": 60, "y": 638, "side": 0 } ] } } }
/// </code>
/// A layer's <c>movie</c> is its package folder under the skin (<c>attract/kidou/kidou</c>); <c>side</c> keeps it to
/// the drum on that side where a screen is built per drum (Song Select's and the dan list's name boards).
/// </summary>
public sealed class SkinScenes
{
    public const string FileName = "scenes.json";

    /// <summary>
    /// Every screen a skin can compose, by its key in scenes.json: the scene id it becomes and the roles it must have.
    /// Gameplay is the layout's (lane, notes, gauge...): its entry only adds layers on top (decoration).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (SceneId Id, string[] Required)> Screens = new Dictionary<string, (SceneId, string[])>(StringComparer.Ordinal)
    {
        ["boot"] = (FlowScenes.Boot, ["boot"]),
        ["entry"] = (FlowScenes.Entry, ["player-entry"]),
        ["song-select"] = (FlowScenes.SongSelect, ["song-select"]),
        ["tutorial"] = (FlowScenes.Tutorial, ["tutorial"]),
        ["dani-select"] = (FlowScenes.DaniSelect, ["dani-select"]),
        ["gameplay"] = (FlowScenes.Gameplay, []),
        ["result"] = (FlowScenes.Result, ["result"]),
        ["dani-result"] = (FlowScenes.Result, ["dani-result"]),
        ["rainbow"] = (new SceneId("rainbow-transition"), [RainbowTransitionComposition.StaticHostId]),
        ["shutter"] = (new SceneId("shutter"), [RainbowTransitionComposition.StaticHostId]),
        ["fade"] = (new SceneId("fade"), [RainbowTransitionComposition.StaticHostId]),
        ["dan-cover"] = (new SceneId("dan-cover"), [RainbowTransitionComposition.StaticHostId]),
        ["dan-exit-cover"] = (new SceneId("dan-exit-cover"), [RainbowTransitionComposition.StaticHostId]),
        ["dan-shutter"] = (new SceneId("dan-shutter"), [RainbowTransitionComposition.StaticHostId]),
    };

    /// <summary>One layer as the skin wrote it.</summary>
    public sealed record Layer(string Movie, string Role, float X, float Y, int? Side)
    {
        /// <summary>The engine's layer: "attract/kidou/kidou" is archive "attract/kidou/packeddata.ddp", movie "kidou/kidou.lm".</summary>
        public SceneLayerDefinition Definition()
        {
            var cut = Movie.LastIndexOf('/');
            var name = Movie[(cut + 1)..];
            return new($"{Movie[..cut]}/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity with { X = X, Y = Y }, Role);
        }
    }

    private readonly Dictionary<string, Layer[]> _scenes;

    private SkinScenes(Dictionary<string, Layer[]> scenes) => _scenes = scenes;

    /// <summary>Screen keys the file composes.</summary>
    public IEnumerable<string> Keys => _scenes.Keys;

    /// <summary>A screen's layers as written, or null when the skin leaves it to the engine.</summary>
    public IReadOnlyList<Layer>? Layers(string key) => _scenes.GetValueOrDefault(key);

    /// <summary>
    /// The screen as a scene: every layer, or for a screen built per drum (<paramref name="sides"/>) the shared layers
    /// then each side's in that order. Null when the skin leaves it to the engine.
    /// </summary>
    public SceneDefinition? Scene(string key, IReadOnlyList<int>? sides = null)
    {
        if (!_scenes.TryGetValue(key, out var layers))
            return null;
        IEnumerable<Layer> chosen = sides is null ? layers
            : [.. layers.Where(static layer => layer.Side is null), .. sides.SelectMany(side => layers.Where(layer => layer.Side == side))];
        return new(SceneDefinition.CurrentVersion, Screens[key].Id, chosen.Select(static layer => layer.Definition()));
    }

    /// <summary>
    /// The first scenes.json among these skin folders (the user's before the bundled one), or null when none has one.
    /// A file that does not read is reported to <paramref name="problem"/> and skipped, so a broken skin falls back
    /// to the engine's screens instead of stopping the game.
    /// </summary>
    public static SkinScenes? Find(IEnumerable<string?> roots, Action<string>? problem = null)
    {
        foreach (var root in roots)
        {
            if (root is null || !File.Exists(Path.Combine(root, FileName)))
                continue;
            try
            {
                return Parse(File.ReadAllText(Path.Combine(root, FileName)));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                problem?.Invoke($"{Path.Combine(root, FileName)}: {error.Message}");
            }
        }
        return null;
    }

    /// <summary>Reads and checks a scenes.json; a mistake throws <see cref="InvalidDataException"/> naming the screen and layer.</summary>
    public static SkinScenes Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != 1)
            throw new InvalidDataException("\"format\": 1 expected.");
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("\"scenes\" object expected.");
        var result = new Dictionary<string, Layer[]>(StringComparer.Ordinal);
        foreach (var scene in scenes.EnumerateObject())
        {
            if (!Screens.TryGetValue(scene.Name, out var screen))
                throw new InvalidDataException($"\"{scene.Name}\" is no screen (known: {string.Join(", ", Screens.Keys)}).");
            if (!scene.Value.TryGetProperty("layers", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{scene.Name}: \"layers\" array expected.");
            var layers = list.EnumerateArray().Select((layer, index) => readLayer(layer, $"{scene.Name} layer {index + 1}")).ToArray();
            foreach (var role in screen.Required)
                if (!layers.Any(layer => layer.Role == role))
                    throw new InvalidDataException($"{scene.Name}: needs a layer with role \"{role}\".");
            result[scene.Name] = layers;
        }
        return new(result);
    }

    private static Layer readLayer(JsonElement layer, string where)
    {
        string text(string name) => layer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } got ? got : throw new InvalidDataException($"{where}: \"{name}\" text expected.");
        float number(string name)
        {
            if (!layer.TryGetProperty(name, out var value))
                return 0;
            if (value.ValueKind != JsonValueKind.Number || !float.IsFinite(value.GetSingle()))
                throw new InvalidDataException($"{where}: \"{name}\" must be a number.");
            return value.GetSingle();
        }
        var movie = text("movie");
        if (movie.Split('/') is not { Length: >= 2 } parts || parts.Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException($"{where}: \"movie\" is a package folder under the skin, like \"entry/entry\".");
        int? side = null;
        if (layer.TryGetProperty("side", out var sideValue))
            side = sideValue.ValueKind == JsonValueKind.Number && sideValue.GetInt32() is 0 or 1 ? sideValue.GetInt32()
                : throw new InvalidDataException($"{where}: \"side\" is 0 (left drum) or 1 (right drum).");
        return new(movie, text("role"), number("x"), number("y"), side);
    }
}
