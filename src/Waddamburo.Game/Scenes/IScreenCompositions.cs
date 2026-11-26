using Waddamburo.Game.Flow;
namespace Waddamburo.Game.Scenes;

/// <summary>
/// A frontend's screens: which movies make each screen the flow shows (scene ids are the flow's, <see cref="FlowScenes"/>).
/// Green's frontend composes them as the game did (traced); Waddamburo's from its own movies only. Screens only one
/// frontend has (AI battle, Waiwai, the reward shop, game over) stay on <see cref="FlowScenes"/>.
/// </summary>
public interface IScreenCompositions
{
    /// <summary>Every screen as first loaded, with the gameplay and results compositions the shell made.</summary>
    IReadOnlyList<SceneDefinition> Initial(IReadOnlyList<SceneDefinition> gameplayAndResults);

    /// <summary>Song Select for the drums playing (<paramref name="sides"/>, the boards' order).</summary>
    SceneDefinition SongSelect(IReadOnlyList<int> sides, bool waiwai = false, bool aiBattle = false);

    /// <summary>The dan dojo's course list for the player on <paramref name="side"/>.</summary>
    SceneDefinition DaniSelect(int side);

    SceneDefinition Results { get; }

    SceneDefinition DaniResults { get; }

    // Intermissions: over the screens, between them.
    SceneDefinition Rainbow { get; }

    SceneDefinition Shutter { get; }

    SceneDefinition Fade { get; }

    SceneDefinition DanCover { get; }

    SceneDefinition DanExitCover { get; }

    SceneDefinition DanShutter { get; }
}
