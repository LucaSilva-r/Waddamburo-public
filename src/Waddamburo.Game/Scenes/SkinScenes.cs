using System.Text.Json;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Scenes;

/// <summary>
/// A skin's screens (<c>scenes.json</c> at the skin's root): which movies make each screen and where. The engine keeps
/// what a screen needs to work (the roles listed in <see cref="Screens"/> for its hosts to drive); everything else is the skin's.
/// <code>
/// { "format": 1, "scenes": { "entry": { "layers": [ { "movie": "entry/entry", "role": "player-entry" },
///   { "movie": "indicator/player_name", "role": "indicator-part", "x": 60, "y": 638, "side": 0 } ] } } }
/// </code>
/// Optional per layer: <c>scaleX</c>, <c>scaleY</c> (1), <c>rotation</c> (degrees, clockwise, about x/y).
/// A layer's <c>movie</c> is its package folder under the skin (<c>attract/kidou/kidou</c>); <c>side</c> keeps it to
/// the drum on that side where a screen is built per drum (Song Select's and the dan list's name boards).
/// <para>Optional per screen: <c>"opensFrom": ["entry"]</c>, the screens it opens from by itself: its movies start from
/// the picture those leave on screen (the Drumhead song wheel starts from the entry's drum), so the switch from them
/// is a cut instead of the fade through black.</para>
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

    /// <summary>One layer as the skin wrote it: placed at (x, y), scaled, then turned by <paramref name="Rotation"/> degrees (clockwise) about that point.</summary>
    /// <param name="Movie">Movie package path relative to the skin folder.</param>
    /// <param name="Role">The role the scene host uses to identify this layer.</param>
    /// <param name="X">Horizontal position on the stage.</param>
    /// <param name="Y">Vertical position on the stage.</param>
    /// <param name="Side">Optional drum side for a per-player layer.</param>
    /// <param name="ScaleX">Horizontal scale factor.</param>
    /// <param name="ScaleY">Vertical scale factor.</param>
    /// <param name="Rotation">Clockwise rotation in degrees about the layer position.</param>
    /// <param name="Anchor">Where it holds on a stage wider or narrower than 1280 (<c>"anchor": "left|center|right|stretch"</c>).</param>
    public sealed record Layer(string Movie, string Role, float X, float Y, int? Side, float ScaleX = 1, float ScaleY = 1, float Rotation = 0,
        Waddamburo.Lumen.Rendering.LumenAnchor Anchor = Waddamburo.Lumen.Rendering.LumenAnchor.Center)
    {
        /// <summary>Where the layer's movie goes on the stage.</summary>
        public LumenMatrix Transform
        {
            get
            {
                var (sin, cos) = MathF.SinCos(Rotation * MathF.PI / 180);
                return new(cos * ScaleX, sin * ScaleX, -sin * ScaleY, cos * ScaleY, X, Y);
            }
        }

        /// <summary>A layer placed by a matrix (no skew): its scale and turn read back from it.</summary>
        public static Layer FromMatrix(string movie, string role, LumenMatrix matrix)
        {
            var scaleX = MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
            var rotation = MathF.Atan2(matrix.M12, matrix.M11) * 180 / MathF.PI;
            var scaleY = scaleX == 0 ? 0 : (matrix.M11 * matrix.M22 - matrix.M21 * matrix.M12) / scaleX;
            return new(movie, role, matrix.X, matrix.Y, null, scaleX, scaleY, MathF.Round(rotation, 3));
        }

        /// <summary>The engine's layer: "attract/kidou/kidou" is archive "attract/kidou/packeddata.ddp", movie "kidou/kidou.lm".</summary>
        public SceneLayerDefinition Definition()
        {
            var cut = Movie.LastIndexOf('/');
            var name = Movie[(cut + 1)..];
            return new($"{Movie[..cut]}/packeddata.ddp", $"{name}/{name}.lm", Transform, Role) { Anchor = Anchor, Side = Side };
        }
    }

    private readonly Dictionary<string, Layer[]> _scenes;
    private readonly Dictionary<string, string[]> _opensFrom;

    private SkinScenes(Dictionary<string, Layer[]> scenes, Dictionary<string, Layer> places, Dictionary<string, string[]> opensFrom) =>
        (_scenes, Places, _opensFrom) = (scenes, places, opensFrom);

    /// <summary>The screens a screen opens from by itself (its <c>opensFrom</c>), by key; empty when it has none.</summary>
    public IReadOnlyList<string> OpensFrom(string key) => _opensFrom.GetValueOrDefault(key) ?? [];

    /// <summary>
    /// Whether the switch from scene <paramref name="from"/> to scene <paramref name="to"/> is a cut: a screen of the
    /// skin that becomes <paramref name="to"/> opens from one that is <paramref name="from"/>.
    /// </summary>
    public bool OpensFrom(SceneId to, SceneId from) => _opensFrom.Any(pair => Screens[pair.Key].Id == to
        && pair.Value.Any(source => Screens[source].Id == from));

    /// <summary>
    /// Where the skin puts gameplay's own movies (gameplay's <c>"places": {"lane": {"x": 248, "y": 271, "rotation": 5}}</c>),
    /// by <see cref="GameplaySceneComposition.PlaceName"/>; a movie left out stays at the layout's place. The notes
    /// follow the hit target's movie (lane_hit): they come to it along its turn and size.
    /// </summary>
    public IReadOnlyDictionary<string, Layer> Places { get; }

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
        var places = new Dictionary<string, Layer>(StringComparer.Ordinal);
        var opensFrom = new Dictionary<string, string[]>(StringComparer.Ordinal);
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
            if (scene.Value.TryGetProperty("opensFrom", out var from))
            {
                if (from.ValueKind != JsonValueKind.Array || from.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException($"{scene.Name}: \"opensFrom\" is a list of screens, like [\"entry\"].");
                var sources = from.EnumerateArray().Select(static item => item.GetString()!).ToArray();
                if (sources.FirstOrDefault(source => !Screens.ContainsKey(source)) is { } unknown)
                    throw new InvalidDataException($"{scene.Name}: \"opensFrom\" names \"{unknown}\", no screen (known: {string.Join(", ", Screens.Keys)}).");
                opensFrom[scene.Name] = sources;
            }
            if (scene.Value.TryGetProperty("places", out var placed))
            {
                if (scene.Name != "gameplay" || placed.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{scene.Name}: \"places\" is gameplay's only, an object of places by movie.");
                foreach (var place in placed.EnumerateObject())
                {
                    if (place.Value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"gameplay places: \"{place.Name}\" is an object (x, y, scaleX, scaleY, rotation).");
                    places[place.Name] = readLayer(place.Value, $"gameplay place {place.Name}", place.Name);
                }
            }
        }
        return new(result, places, opensFrom);
    }

    private static Layer readLayer(JsonElement layer, string where, string? place = null)
    {
        string text(string name) => layer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } got ? got : throw new InvalidDataException($"{where}: \"{name}\" text expected.");
        float number(string name, float otherwise = 0)
        {
            if (!layer.TryGetProperty(name, out var value))
                return otherwise;
            if (value.ValueKind != JsonValueKind.Number || !float.IsFinite(value.GetSingle()))
                throw new InvalidDataException($"{where}: \"{name}\" must be a number.");
            return value.GetSingle();
        }
        var movie = place ?? text("movie");
        if (place is null && (movie.Split('/') is not { Length: >= 2 } parts || parts.Any(static part => part is "" or "." or "..")))
            throw new InvalidDataException($"{where}: \"movie\" is a package folder under the skin, like \"entry/entry\".");
        int? side = null;
        if (layer.TryGetProperty("side", out var sideValue))
            side = sideValue.ValueKind == JsonValueKind.Number && sideValue.GetInt32() is 0 or 1 ? sideValue.GetInt32()
                : throw new InvalidDataException($"{where}: \"side\" is 0 (left drum) or 1 (right drum).");
        var (scaleX, scaleY) = (number("scaleX", 1), number("scaleY", 1));
        if (scaleX == 0 || scaleY == 0)
            throw new InvalidDataException($"{where}: a scale of 0 hides the layer; remove the layer instead.");
        Waddamburo.Lumen.Rendering.LumenAnchor anchor;
        try
        {
            anchor = Waddamburo.Lumen.Rendering.LumenStage.Parse(layer.TryGetProperty("anchor", out var anchorValue) ? anchorValue.GetString() : null);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException($"{where}: {error.Message}");
        }
        return new(movie, place is null ? text("role") : "place", number("x"), number("y"), side, scaleX, scaleY, number("rotation"), anchor);
    }
}
