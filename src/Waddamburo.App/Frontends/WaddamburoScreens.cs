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

    // Packages keep the folder layout the movie editor and free_movies use (<area>/<movie>).
    private static SceneLayerDefinition movie(string area, string name, string host, float x = 0, float y = 0) =>
        new($"{area}/packeddata.ddp", $"{name}/{name}.lm", LumenMatrix.Identity with { X = x, Y = y }, host);

    // A player's name board: bottom left for the left drum, bottom right for the right one (FlowScenes.BoardSide).
    private static SceneLayerDefinition board(int side) => movie("indicator", "player_name", LayerRoles.IndicatorPart, side == 1 ? 948 : 60, 638);

    private static SceneDefinition screen(SceneId id, params SceneLayerDefinition[] layers) => new(SceneDefinition.CurrentVersion, id, layers);

    public IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults) =>
    [
        screen(FlowScenes.Boot, movie("attract/kidou", "kidou", "boot")),
        // The entry: who plays, and the mode (normal or dan). Its boards show each drum's player.
        screen(FlowScenes.Entry, movie("entry", "entry", "player-entry"), board(0), board(1)),
        SongSelect([0]),
        .. gameplayAndResults,
        screen(FlowScenes.Tutorial, movie("tutorial", "tutorial", "tutorial")),
        DaniSelect(0),
    ];

    // ponytail: one Song Select; Waddamburo's frontend has no Waiwai or AI battle one.
    public SceneDefinition SongSelect(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false) =>
        screen(FlowScenes.SongSelect, [movie("song_select", "song_select", "song-select"), .. sides.Select(board)]);

    public SceneDefinition DaniSelect(int side) =>
        screen(FlowScenes.DaniSelect, movie("dani_select", "dani_select", "dani-select"), movie("indicator", "player_name", side == 1 ? "dani-board-1" : "dani-board-0", side == 1 ? 948 : 60, 638));

    /// <summary>
    /// The gameplay roles only (lane, gauge, notes, rolls, balloons, hit effects, combo, song info, name board) at the
    /// layout's places: none of Green's scenery, no song skins. ponytail: the roles and places are still the game's
    /// gameplay contract (Green's composition filtered); our own gameplay composition when our movies outgrow it.
    /// </summary>
    public SceneDefinition Gameplay(SceneId id, Random random, Formats.Layout.EnsoLayout layout, GameplaySceneComposition.ThemedSkin? theme = null,
        int side = 0, bool twoPlayers = false, bool dojo = false)
    {
        var all = GameplaySceneComposition.Create(id, random, layout, null, side, twoPlayers, dojo);
        return new(SceneDefinition.CurrentVersion, id, all.Layers.Where(static layer => !GameplaySceneComposition.IsGreenDecoration(layer)));
    }

    public SceneDefinition Results { get; } = screen(FlowScenes.Result, movie("enso_result", "result", "result"));

    public SceneDefinition DaniResults { get; } = screen(FlowScenes.Result, movie("dani_result", "dani_result", "dani-result"));

    public SceneDefinition Rainbow { get; } = RainbowTransitionComposition.Create(new SceneId("rainbow-transition"));

    public SceneDefinition Shutter { get; } = transition("shutter", "shutter");

    public SceneDefinition Fade { get; } = transition("fade", "scene_change_fade");

    public SceneDefinition DanCover { get; } = transition("dan-cover", "scene_change_dani1");

    public SceneDefinition DanExitCover { get; } = transition("dan-exit-cover", "scene_change_dani2");

    public SceneDefinition DanShutter { get; } = transition("dan-shutter", "dani_shutter");

    private static SceneDefinition transition(string id, string name) =>
        screen(new SceneId(id), movie("intermission", name, RainbowTransitionComposition.StaticHostId));
}
