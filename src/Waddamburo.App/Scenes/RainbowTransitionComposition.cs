using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

internal static class RainbowTransitionComposition
{
    public const string StaticHostId = "rainbow-transition";
    public const string SongTitleFill = "scene_change_song_name";
    public const string CoverLabel = "in_extra";
    public const string RevealLabel = "out_extra";

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
