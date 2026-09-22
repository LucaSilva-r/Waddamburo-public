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

    // enso_original part families and their variant counts (Green data). The game picks each
    // part independently per song; songs with a themed skin use that skin instead.
    private static readonly (string Part, int Variants, string Suffix)[] OriginalParts =
    [
        ("bg_nomal", 5, ""), ("bg_fever", 4, ""), ("dance", 22, ""), ("dodai", 3, ""),
        ("fever", 4, ""), ("donbg", 6, "_1p"), ("chibi", 14, ""), ("renda", 3, ""),
    ];

    // Skin roles bottom to top, with their stage offsets. ponytail: dodai/renda/chibi offsets
    // come from the movies' authored bounds, not from a verified layout.
    private static readonly (string Role, float Y)[] SkinRoles =
    [
        ("bg_nomal", 360), ("bg_fever", 360), ("dance", 360), ("dodai", 656),
        ("renda", 0),
        // Full-gauge jumpers rise from below the screen (reference frame: y 750+).
        ("fever", 360),
        ("donbg", 0), ("chibi", 0),
    ];

    /// <summary>
    /// Gameplay scene with either a themed skin (one archive holding every part, given with its
    /// movie list) or, without one, a random enso_original mix.
    /// </summary>
    public static SceneDefinition Create(SceneId id, Random random, ThemedSkin? theme = null)
    {
        SceneLayerDefinition? skin(string role, float y)
        {
            if (theme is not null)
                return theme.Movies.FirstOrDefault(movie => Role(Path.GetFileNameWithoutExtension(movie)) == role)
                    is { } movie ? layer(theme.Archive, movie, y) : null;
            var entry = OriginalParts.Single(entry => entry.Part == role);
            var name = $"{role}_a_{random.Next(1, entry.Variants + 1):00}{entry.Suffix}";
            return layer($"enso_original/{name}/packeddata.ddp", $"{name}/{name}.lm", y);
        }
        return new(
            SceneDefinition.CurrentVersion,
            id,
            [
                .. SkinRoles.Select(entry => skin(entry.Role, entry.Y)).OfType<SceneLayerDefinition>(),
                // ponytail: combo bonus and kusudama are placed at the stage origin like the balloon
                // overlay; unverified against the game.
                // The player's character version (Don 1P); base1p's generic one is not used in play.
                layer("enso_system/don1p/packeddata.ddp", "combo_bonus_don_1p/combo_bonus_don_1p.lm"),
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
                layer("enso_system/common/packeddata.ddp", "onp_kusudama/onp_kusudama.lm"),
                layer("enso_system/common/packeddata.ddp", "renda_num/renda_num.lm", x: 190, y: 184),
                layer("enso_system/base1p/packeddata.ddp", "action_fusen_1p/action_fusen_1p.lm"),
                layer("enso_system/common/packeddata.ddp", "action_kusudama/action_kusudama.lm"),
                layer("enso_system/common/packeddata.ddp", "don3d/don3d.lm", x: 200, y: 92),
                layer("enso_system/common/packeddata.ddp", "lane_syousetsu/lane_syousetsu.lm"),
                layer("enso_system/don1p/packeddata.ddp", "onp_kiseki_don_1p/onp_kiseki_don_1p.lm", y: 184),
            ]);
    }

    /// <summary>A themed gameplay skin: its archive and the movies it contains.</summary>
    public sealed record ThemedSkin(string Archive, IReadOnlyList<string> Movies);

    /// <summary>Skin role of a gameplay layer name ("bg_nomal_a_02", "donbg_b_01_1p" -> role); 2P parts keep their name.</summary>
    public static string Role(string layerName) =>
        System.Text.RegularExpressions.Regex.Match(layerName, "^(.+?)_[ab]_[0-9]+(?:_1p|_common)?$") is { Success: true } match
            ? match.Groups[1].Value
            : layerName;

    private static SceneLayerDefinition layer(string archive, string movie, float y = 0, float x = 0) => new(
        archive,
        movie,
        LumenMatrix.Identity with { X = x, Y = y },
        StaticHostId);
}
