using Waddamburo.Game.Scenes;

namespace Waddamburo.Skins.Green;

/// <summary>Green's frontend's screens: the game's traced compositions.</summary>
// ponytail: the compositions still live in Game's FlowScenes; they move here once nothing else reads them.
public sealed class GreenScreens : IScreenCompositions
{
    public static GreenScreens Instance { get; } = new();

    public IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults) => GreenScenes.Initial(gameplayAndResults);

    public SceneDefinition SongSelect(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false) =>
        GreenScenes.SongSelectScene(sides, waiwai, aiBattle);

    public SceneDefinition DaniSelect(int side) => GreenScenes.DaniSelectScene(side);

    public SceneDefinition Gameplay(Waddamburo.Game.Flow.SceneId id, Random random, Waddamburo.Formats.Layout.EnsoLayout layout,
        GameplaySceneComposition.ThemedSkin? theme = null, int side = 0, bool twoPlayers = false, bool dojo = false) =>
        GameplaySceneComposition.Create(id, random, layout, theme, side, twoPlayers, dojo);

    public SceneDefinition Results => GreenScenes.Results;

    public SceneDefinition DaniResults => GreenScenes.DaniResults;

    public SceneDefinition Rainbow => GreenScenes.Rainbow;

    public SceneDefinition Shutter => GreenScenes.Shutter;

    public SceneDefinition Fade => GreenScenes.Fade;

    public SceneDefinition DanCover => GreenScenes.DanCover;

    public SceneDefinition DanExitCover => GreenScenes.DanExitCover;

    public SceneDefinition DanShutter => GreenScenes.DanShutter;
}
