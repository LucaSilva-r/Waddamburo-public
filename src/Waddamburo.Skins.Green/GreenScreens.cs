using Waddamburo.Game.Scenes;

namespace Waddamburo.Skins.Green;

/// <summary>Green's frontend's screens: the game's traced compositions.</summary>
// ponytail: the compositions still live in Game's FlowScenes; they move here once nothing else reads them.
public sealed class GreenScreens : IScreenCompositions
{
    public static GreenScreens Instance { get; } = new();

    public IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults) => FlowScenes.Initial(gameplayAndResults);

    public SceneDefinition SongSelect(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false) =>
        FlowScenes.SongSelectScene(sides, waiwai, aiBattle);

    public SceneDefinition DaniSelect(int side) => FlowScenes.DaniSelectScene(side);

    public SceneDefinition Results => FlowScenes.Results;

    public SceneDefinition DaniResults => FlowScenes.DaniResults;

    public SceneDefinition Rainbow => FlowScenes.Rainbow;

    public SceneDefinition Shutter => FlowScenes.Shutter;

    public SceneDefinition Fade => FlowScenes.Fade;

    public SceneDefinition DanCover => FlowScenes.DanCover;

    public SceneDefinition DanExitCover => FlowScenes.DanExitCover;

    public SceneDefinition DanShutter => FlowScenes.DanShutter;
}
