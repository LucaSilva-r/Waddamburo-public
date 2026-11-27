using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's frontend's screens: its own movies (the packages in movies/), each screen only the layers it shows. No
/// attract loop, no legend or countdown parts, no Waiwai or AI battle screens. A package that does not exist yet shows
/// as a placeholder (DirectoryLumenMovieContentSource.PackagesOnly); none of the game's movies ever loads.
/// </summary>
internal sealed class WaddamburoScreens : IScreenCompositions
{
    public static WaddamburoScreens Instance { get; } = new();

    /// <summary>The skin's scenes.json (null: none, every screen as below). A screen the file has replaces the one here.</summary>
    public SkinScenes? Skin
    {
        get;
        set
        {
            field = value;
            _made.Clear();
        }
    }

    // A screen stays the same object while the skin does (overlays and the catalog may hold on to one); per drum ones are new each time.
    private readonly Dictionary<string, SceneDefinition> _made = new(StringComparer.Ordinal);

    private SceneDefinition skin(string key, SceneDefinition builtIn, IReadOnlyList<int>? sides = null)
    {
        if (sides is not null)
            return Skin?.Scene(key, sides) ?? builtIn;
        lock (_made)
            return _made.TryGetValue(key, out var made) ? made : _made[key] = Skin?.Scene(key) ?? builtIn;
    }

    // Packages keep the folder layout the movie editor and free_movies use (<area>/<movie>).
    private static SceneLayerDefinition movie(string area, string name, string host, float x = 0, float y = 0) =>
        new($"{area}/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity with { X = x, Y = y }, host);

    // A player's name board: bottom left for the left drum, bottom right for the right one (FlowScenes.BoardSide).
    private static SceneLayerDefinition board(int side) => movie("indicator", "player_name", LayerRoles.IndicatorPart, side == 1 ? 948 : 60, 638);

    private static SceneDefinition screen(SceneId id, params SceneLayerDefinition[] layers) => new(SceneDefinition.CurrentVersion, id, layers);

    public IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults) =>
    [
        skin("boot", screen(FlowScenes.Boot, movie("attract/kidou", "kidou", "boot"))),
        // The entry: who plays, and the mode (normal or dan). Its boards show each drum's player.
        skin("entry", screen(FlowScenes.Entry, movie("entry", "entry", "player-entry"), board(0), board(1))),
        SongSelect([0]),
        .. gameplayAndResults,
        skin("tutorial", screen(FlowScenes.Tutorial, movie("tutorial", "tutorial", "tutorial"))),
        DaniSelect(0),
    ];

    // ponytail: one Song Select; Waddamburo's frontend has no Waiwai or AI battle one.
    public SceneDefinition SongSelect(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false) =>
        skin("song-select", screen(FlowScenes.SongSelect, [movie("song_select", "song_select", "song-select"), .. sides.Select(board)]), sides);

    public SceneDefinition DaniSelect(int side) => skin("dani-select",
        screen(FlowScenes.DaniSelect, movie("dani_select", "dani_select", "dani-select"), movie("indicator", "player_name", side == 1 ? "dani-board-1" : "dani-board-0", side == 1 ? 948 : 60, 638)), [side]);

    /// <summary>
    /// The gameplay roles only (lane, gauge, notes, rolls, balloons, hit effects, combo, song info, name board) at the
    /// layout's places: none of Green's scenery, no song skins. ponytail: the roles and places are still the game's
    /// gameplay contract (Green's composition filtered); our own gameplay composition when our movies outgrow it.
    /// </summary>
    public SceneDefinition Gameplay(SceneId id, Random random, Formats.Layout.EnsoLayout layout, GameplaySceneComposition.ThemedSkin? theme = null,
        int side = 0, bool twoPlayers = false, bool dojo = false)
    {
        // The skin's layout points over the layout given (the game data's, else the standard one).
        var all = GameplaySceneComposition.Create(id, random, Skin is { } skin ? layout.With(skin.Layout) : layout, null, side, twoPlayers, dojo);
        // The skin's gameplay entry adds layers over the layout's (decoration): the roles above stay the layout's.
        return new(SceneDefinition.CurrentVersion, id, [.. all.Layers.Where(static layer => !GameplaySceneComposition.IsGreenDecoration(layer)),
            .. (Skin?.Layers("gameplay") ?? []).Select(static layer => layer.Definition())]);
    }

    public SceneDefinition Results => skin("result", BuiltInResults);

    private static readonly SceneDefinition BuiltInResults = screen(FlowScenes.Result, movie("enso_result", "result", "result"));

    public SceneDefinition DaniResults => skin("dani-result", BuiltInDaniResults);

    private static readonly SceneDefinition BuiltInDaniResults = screen(FlowScenes.Result, movie("dani_result", "dani_result", "dani-result"));

    public SceneDefinition Rainbow => skin("rainbow", RainbowTransitionComposition.Create(new SceneId("rainbow-transition")));

    public SceneDefinition Shutter => skin("shutter", transition("shutter", "shutter"));

    public SceneDefinition Fade => skin("fade", transition("fade", "scene_change_fade"));

    public SceneDefinition DanCover => skin("dan-cover", transition("dan-cover", "scene_change_dani1"));

    public SceneDefinition DanExitCover => skin("dan-exit-cover", transition("dan-exit-cover", "scene_change_dani2"));

    public SceneDefinition DanShutter => skin("dan-shutter", transition("dan-shutter", "dani_shutter"));

    private static SceneDefinition transition(string id, string name) =>
        screen(new SceneId(id), movie("intermission", name, RainbowTransitionComposition.StaticHostId));
}
