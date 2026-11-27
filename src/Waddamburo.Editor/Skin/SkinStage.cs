using System.Runtime.InteropServices;
using Waddamburo.Game;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>
/// Plays a skin's packages for the editor: whole screens (every layer of a scene, in order) and single packages, drawn
/// into preview textures for the home page's thumbnails, the scene view and the live scene window. The package open in
/// the component editor plays from its unsaved document (<see cref="SetLive"/>), so every preview follows each edit.
/// </summary>
public sealed class SkinStage
{
    private readonly SdlApplication _application;
    private readonly Func<byte[], (int Width, int Height, byte[] Rgba)?> _decode;
    private readonly ILumenHostBinding? _binding;
    private readonly Func<LumenNativeSurfaceKey, RenderTextureId?> _native;
    private readonly Dictionary<string, (int Version, LumenMovieContent Content, RenderTextureId[] Textures)> _contents = new(StringComparer.Ordinal);
    private readonly Dictionary<byte[], RenderTextureId> _uploaded = new(ReferenceEqualityComparer.Instance);
    private (string Folder, Func<string> Json, Func<string, byte[]> ReadFile)? _live;
    private int _liveVersion;

    public SkinStage(SdlApplication application, string root, Func<byte[], (int Width, int Height, byte[] Rgba)?> decode,
        ILumenHostBinding? binding, Func<LumenNativeSurfaceKey, RenderTextureId?> native)
    {
        _application = application;
        Root = Path.GetFullPath(root);
        _decode = decode;
        _binding = binding;
        _native = native;
    }

    public string Root { get; }

    /// <summary>
    /// The skin folder to edit when none is named: the nearest movies/ with a skin.json up from the current folder (a
    /// checkout's skin submodule), else the one beside the executable.
    /// </summary>
    public static string FindRoot()
    {
        for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "movies", "skin.json")))
                return Path.Combine(folder.FullName, "movies");
        return Path.Combine(AppContext.BaseDirectory, "movies");
    }

    /// <summary>The running game this editor was started from (F7 in the game), whose movies' data the previews follow; null: none.</summary>
    public SkinLive? Live
    {
        get;
        set
        {
            field = value;
            if (value is not null) value.ScreenChanged += _ => Version++;
        }
    }

    /// <summary>Bumped whenever something a preview shows changed (a package saved or edited, scenes.json).</summary>
    public int Version { get; private set; }

    /// <summary>Every package of the skin by folder ("entry/entry", "attract/kidou/kidou"), sorted.</summary>
    public IReadOnlyList<string> Packages() => [.. Directory.EnumerateFiles(Root, Formats.Package.MoviePackage.FileName, SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(Root, Path.GetDirectoryName(file)!).Replace('\\', '/'))
        .Where(static folder => !folder.StartsWith('.'))
        .Order(StringComparer.Ordinal)];

    public bool Exists(string folder) => File.Exists(Path.Combine(Root, folder, Formats.Package.MoviePackage.FileName));

    /// <summary>The package open in the component editor plays from its document (null: none, every package from disk).</summary>
    public void SetLive(string? folder, Func<string>? json = null, Func<string, byte[]>? readFile = null)
    {
        _live = folder is null ? null : (folder, json!, readFile!);
        LiveChanged();
    }

    /// <summary>The live package's document changed.</summary>
    public void LiveChanged()
    {
        _liveVersion++;
        Version++;
    }

    /// <summary>Files on disk changed (a save, an outside editor): every package is read again.</summary>
    public void Reload()
    {
        _contents.Clear();
        Version++;
    }

    private (int Version, LumenMovieContent Content, RenderTextureId[] Textures)? content(string folder, string role)
    {
        var live = _live is { } open && open.Folder == folder;
        var version = live ? _liveVersion : 0;
        if (_contents.TryGetValue(folder, out var cached) && cached.Version == version)
            return cached;
        LumenMovieContent loaded;
        try
        {
            loaded = !Exists(folder) ? LumenMovieContent.Placeholder(folder, $"{folder} (no package yet)")
                : live ? LumenMovieContent.LoadPackage(Path.Combine(Root, folder), _decode, scriptErrors: [], movieJson: _live!.Value.Json(), readFile: _live.Value.ReadFile)
                : LumenMovieContent.LoadPackage(Path.Combine(Root, folder), _decode, scriptErrors: []);
        }
        catch (Exception error) when (error is FormatException or IOException or InvalidDataException or InvalidOperationException)
        {
            loaded = LumenMovieContent.Placeholder(folder, $"{folder}: {error.Message}");
        }
        var textures = loaded.Textures.Select(texture =>
        {
            var pixels = ImmutableCollectionsMarshal.AsArray(texture.Rgba8)!;
            return _uploaded.TryGetValue(pixels, out var id) ? id
                : _uploaded[pixels] = _application.UploadRgba8(checked((uint)texture.Width), checked((uint)texture.Height), pixels);
        }).ToArray();
        return _contents[folder] = (version, loaded, textures);
    }

    /// <summary>Which drums have joined, as each player sees a screen (a screen built per drum shows each joined drum's parts).</summary>
    public enum Players { Left, Right, Both }

    /// <summary>A screen as one kind of credit sees it: who plays, and (gameplay) a dan course's lane.</summary>
    public sealed record ScreenVariant(Players Players = Players.Left, bool Dojo = false)
    {
        /// <summary>Whether a layer kept to a drum's side (null: every credit's) shows.</summary>
        public bool Shows(int? side) => side is not { } drum || Players == Players.Both || drum == (Players == Players.Right ? 1 : 0);
    }

    private ScreenVariant _variant = new();

    /// <summary>The variant every screen is played in (previews, thumbnails, the scene view); another one plays them again.</summary>
    public ScreenVariant Variant
    {
        get => _variant;
        set
        {
            if (value == _variant)
                return;
            _variant = value;
            Version++;
        }
    }

    /// <summary>
    /// The layers the engine places on a screen itself, under the skin's (gameplay: the layout's lane, gauge, notes...),
    /// as skin folders (set by the host). Null or empty: the screen is the skin's layers alone.
    /// </summary>
    public Func<string, IReadOnlyList<SkinScenes.Layer>>? Fixed { get; set; }

    /// <summary>A screen: the engine's layers (<see cref="Fixed"/>), then the skin's. <paramref name="reuse"/>: a scene whose players carry on where unchanged.</summary>
    public Scene PlayScreen(string key, IReadOnlyList<SkinScenes.Layer> layers, Scene? reuse = null, PreviewScenario? scenario = null)
    {
        var under = Fixed?.Invoke(key) ?? [];
        return new(this, [.. under, .. layers], reuse, fixedCount: under.Count, scenario);
    }

    /// <summary>
    /// Gives a movie playing on its own (the component editor's) the example data of the part it plays in the skin's
    /// screens (Song Select's list, a name board's drum...); the action to run every tick, if it needs one.
    /// </summary>
    public Action? GiveExampleData(LumenPlayer player, string folder, IEnumerable<SkinScenes.Layer> uses)
    {
        if (Live?.Holds(folder) == true)
        {
            Live.Follow(player, folder);
            return null;
        }
        var use = uses.FirstOrDefault(layer => layer.Movie == folder);
        return PreviewData.Attach(player, use?.Role ?? "", fresh: true, use?.Side);
    }

    /// <summary>One package alone, fitted to the stage (a component's thumbnail): played for a moment, then measured.</summary>
    public Scene PlayFitted(string folder, int ticks)
    {
        var layer = new SkinScenes.Layer(folder, "preview", 0, 0, null);
        var scene = new Scene(this, [layer], null);
        scene.Advance(ticks);
        if (!scene.Bounds(0, out var box) || box.Width < 1 || box.Height < 1)
            return scene;
        var scale = Math.Min(4, Math.Min(1280 * .9f / box.Width, 720 * .9f / box.Height));
        var fitted = new Scene(this, [layer with { ScaleX = scale, ScaleY = scale, X = 640 - (box.X + box.Width / 2) * scale, Y = 360 - (box.Y + box.Height / 2) * scale }], null);
        fitted.Advance(ticks);
        return fitted;
    }

    /// <summary>A composed screen: its players, its textures, a frame of it.</summary>
    public sealed class Scene
    {
        private readonly SkinStage _stage;
        private readonly LumenScenePlayer _player;
        private readonly RenderTextureId[] _textures;
        private readonly (string Folder, int Version, LumenPlayer Player)[] _made;
        private readonly bool[] _hidden;
        private readonly List<Action> _ticks = [];

        private readonly PreviewScenario? _scenario;
        private readonly string? _liveScreen; // the game's screen when built (live data in): another one, every player again
        private double _clock;

        internal Scene(SkinStage stage, IReadOnlyList<SkinScenes.Layer> layers, Scene? reuse, int fixedCount = 0, PreviewScenario? scenario = null)
        {
            _stage = stage;
            _scenario = scenario;
            _liveScreen = stage.Live?.Screen;
            // Another scenario, or the game on another screen: every movie starts again with its data.
            if (reuse?._scenario != scenario || reuse?._liveScreen != _liveScreen) reuse = null;
            _clock = reuse?._clock ?? 0;
            FixedCount = fixedCount;
            var sceneLayers = new List<LumenSceneLayer>();
            var textures = new List<RenderTextureId>();
            _made = new (string, int, LumenPlayer)[layers.Count];
            _hidden = new bool[layers.Count];
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                var (version, content, ids) = stage.content(layer.Movie, layer.Role)!.Value;
                // The same package, unchanged: it carries on (only the edited one restarts), with its example data.
                var kept = reuse?._made.FirstOrDefault(made => made.Folder == layer.Movie && made.Version == version && made.Player is not null
                    && !_made.Any(taken => ReferenceEquals(taken.Player, made.Player))).Player;
                var player = kept ?? fresh(content);
                // The game's own data where it shows this movie (and no scenario was chosen), else the example data.
                if (scenario is null && stage.Live?.Holds(layer.Movie) == true)
                {
                    if (kept is null) stage.Live.Follow(player, layer.Movie);
                }
                else
                {
                    // The second player's lane (two players) is the right drum's: its name, its data.
                    var side = layer.Side ?? (layer.Role == Waddamburo.Game.Scenes.GameplaySceneComposition.PlayerTwoHostId ? 1 : null);
                    if (PreviewData.Attach(player, layer.Role, fresh: kept is null, side) is { } tick)
                        _ticks.Add(tick);
                    if (kept is null)
                        scenario?.Apply(player);
                }
                _made[index] = (layer.Movie, version, player);
                // Another drum's part in this variant: kept in place (the layer list's order), unseen and unpicked.
                _hidden[index] = index >= fixedCount && !stage.Variant.Shows(layer.Side);
                sceneLayers.Add(new(player, layer.Transform, (uint)textures.Count, (uint)ids.Length) { Alpha = _hidden[index] ? 0 : 1, Anchor = layer.Anchor });
                textures.AddRange(ids);
            }
            _player = new LumenScenePlayer(1280, 720, sceneLayers);
            _textures = [.. textures];
        }

        /// <summary>Whether a layer is another drum's part in this variant (not drawn).</summary>
        public bool Hidden(int layer) => layer >= 0 && layer < _hidden.Length && _hidden[layer];

        /// <summary>How many of the layers are the engine's (<see cref="Fixed"/>), before the skin's.</summary>
        public int FixedCount { get; }

        private LumenPlayer fresh(LumenMovieContent content) => content.CreatePlayer(hostBinding: _stage._binding);

        public void Advance(int ticks = 1)
        {
            for (var tick = 0; tick < ticks; tick++)
            {
                foreach (var each in _ticks)
                    each();
                if (_scenario is { } playing)
                {
                    foreach (var (name, drum, arguments) in playing.Due(_clock, _clock + 1.0 / 60))
                        foreach (var layer in _made)
                            layer.Player.EmitFor(drum, name, arguments);
                    _clock += 1.0 / 60;
                }
                _player.Advance();
            }
        }

        /// <summary>What a layer covers, in its own movie's coordinates (the layer's transform not applied).</summary>
        public bool Bounds(int layer, out LumenNativeSurfacePlacement box)
        {
            box = default;
            return layer >= 0 && layer < _made.Length && !_hidden[layer] && _made[layer].Player.TryInspectBounds("", out box);
        }

        /// <summary>A frame of the screen; <paramref name="view"/>: the part of the stage it shows (null: the 1280x720 screen).</summary>
        public RenderFrame Frame(RenderColor? clear = null, (float X, float Y, float Width, float Height)? view = null)
        {
            var frame = LumenRenderFrameAdapter.Compose(
                _player.CreateRenderSnapshot(1), clear ?? new RenderColor(.08f, .09f, .1f, 1),
                index => index < _textures.Length ? _textures[index] : _textures.FirstOrDefault(), _stage._native);
            if (view is not { } part)
                return frame;
            RenderVertex at(RenderVertex vertex) => vertex with
            {
                X = (vertex.X * 1280 - part.X) / part.Width,
                Y = (vertex.Y * 720 - part.Y) / part.Height,
            };
            return new RenderFrame(frame.ClearColor, frame.Quads.Select(quad => quad with
            {
                TopLeft = at(quad.TopLeft), TopRight = at(quad.TopRight), BottomRight = at(quad.BottomRight), BottomLeft = at(quad.BottomLeft),
            }), part.Width / part.Height);
        }
    }
}
