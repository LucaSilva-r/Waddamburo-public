using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

/// <summary>
/// Product-owned composition for the initial one-player gameplay presentation.
/// These Lumen layers are decorative and host-free; notes and judgement remain
/// native gameplay state rendered above the lane in a later integration step.
/// </summary>
internal static class GameplaySceneComposition
{
    public const string StaticHostId = "gameplay-static";

    public static SceneDefinition Create(SceneId id) => new(
        SceneDefinition.CurrentVersion,
        id,
        [
            layer(
                "enso_A3/packeddata.ddp",
                "bg_nomal_b_32/bg_nomal_b_32.lm",
                y: 360),
            layer(
                "enso_A3/packeddata.ddp",
                "donbg_b_32_common/donbg_b_32_common.lm"),
            layer(
                "enso_system/common/packeddata.ddp",
                "lane/lane.lm",
                y: 184),
        ]);

    private static SceneLayerDefinition layer(string archive, string movie, float y = 0) => new(
        archive,
        movie,
        LumenMatrix.Identity with { Y = y },
        StaticHostId);
}
