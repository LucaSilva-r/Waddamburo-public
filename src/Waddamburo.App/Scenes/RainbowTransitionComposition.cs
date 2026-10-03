using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Scenes;

internal static class RainbowTransitionComposition
{
    public const string StaticHostId = "rainbow-transition";
    public const string SongTitleFill = "scene_change_song_name";
    public const string CoverLabel = "in_extra";
    public const string RevealLabel = "out_extra";
    /// <summary>The rainbow without the song title (a library switch).</summary>
    public const string PlainCoverLabel = "in";
    public const string PlainRevealLabel = "out";

    /// <summary>The song's cover (in_extra), or the plain one where the movie predates it (before Murasaki).</summary>
    public static void Cover(LumenPlayer player) =>
        player.GotoLabel(player.Labels.ContainsKey(CoverLabel) ? CoverLabel : PlainCoverLabel, play: true);

    public static void Reveal(LumenPlayer player) =>
        player.GotoLabel(player.Labels.ContainsKey(RevealLabel) ? RevealLabel : PlainRevealLabel, play: true);

    public static SceneDefinition Create(SceneId id) => new(
        SceneDefinition.CurrentVersion,
        id,
        [
            new SceneLayerDefinition(
                "intermission/packeddata.ddp",
                "scene_change_rainbow/scene_change_rainbow.lm",
                LumenMatrix.Identity,
                StaticHostId),
        ]);
}
