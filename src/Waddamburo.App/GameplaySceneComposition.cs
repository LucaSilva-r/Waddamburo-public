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
            layer("enso_A3/packeddata.ddp", "bg_fever_b_32/bg_fever_b_32.lm", y: 360),
            layer(
                "enso_A3/packeddata.ddp",
                "donbg_b_32_common/donbg_b_32_common.lm"),
            layer(
                "enso_system/common/packeddata.ddp",
                "lane/lane.lm",
                y: 184),
            // One gauge per clear line; gameplay shows the course's one.
            layer("enso_system/don1p/packeddata.ddp", "gage_don_1p_easy/gage_don_1p_easy.lm", y: 184),
            layer("enso_system/don1p/packeddata.ddp", "gage_don_1p_normal/gage_don_1p_normal.lm", y: 184),
            layer("enso_system/don1p/packeddata.ddp", "gage_don_1p_hard/gage_don_1p_hard.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_hit/lane_hit.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_hit_effect/lane_hit_effect.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "lane_obi/lane_obi.lm", y: 184),
            layer("enso_system/common/packeddata.ddp", "onp_don/onp_don.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_katsu/onp_katsu.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_don_dai/onp_don_dai.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_katsu_dai/onp_katsu_dai.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_renda/onp_renda.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_renda_dai/onp_renda_dai.lm"),
            layer("enso_system/common/packeddata.ddp", "onp_fusen/onp_fusen.lm"),
            layer("enso_system/common/packeddata.ddp", "renda_num/renda_num.lm", x: 190, y: 184),
            layer("enso_system/base1p/packeddata.ddp", "action_fusen_1p/action_fusen_1p.lm"),
            layer("enso_system/common/packeddata.ddp", "don3d/don3d.lm", x: 200, y: 92),
            layer("enso_system/common/packeddata.ddp", "lane_syousetsu/lane_syousetsu.lm"),
            layer("enso_system/don1p/packeddata.ddp", "onp_kiseki_don_1p/onp_kiseki_don_1p.lm", y: 184),
        ]);

    private static SceneLayerDefinition layer(string archive, string movie, float y = 0, float x = 0) => new(
        archive,
        movie,
        LumenMatrix.Identity with { X = x, Y = y },
        StaticHostId);
}
