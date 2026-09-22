using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

/// <summary>
/// Product-owned composition for the initial one-player gameplay presentation.
/// Asset choices and layout are composition data; gameplay uses semantic movie roles.
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
            layer("enso_A3/packeddata.ddp", "dance_b_32/dance_b_32.lm", y: 360),
            layer(
                "enso_A3/packeddata.ddp",
                "donbg_b_32_common/donbg_b_32_common.lm"),
            layer(
                "enso_system/common/packeddata.ddp",
                "lane/lane.lm",
                y: 184),
            layer("enso_system/don1p/packeddata.ddp", "gage_don_1p_normal/gage_don_1p_normal.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_hit/lane_hit.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_hit_effect/lane_hit_effect.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_obi/lane_obi.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "onp_don/onp_don.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_katsu/onp_katsu.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_don_dai/onp_don_dai.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_katsu_dai/onp_katsu_dai.lm"),
            layer("enso_system/common/packeddata.ddp", "lane_syousetsu/lane_syousetsu.lm"),
        ]);

    private static SceneLayerDefinition layer(string archive, string movie, float y = 0) => new(
        archive,
        movie,
        LumenMatrix.Identity with { Y = y },
        StaticHostId);
}
