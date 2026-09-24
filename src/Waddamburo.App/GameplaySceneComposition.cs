using Waddamburo.Formats.Layout;
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

    /// <summary>Host id of the second player's lane movies in two-player gameplay.</summary>
    public const string PlayerTwoHostId = "gameplay-static-2p";

    // Movies both players share in two-player gameplay (one instance, drawn over both lanes; the
    // kusudama counts for both). Waiwai adds its effect overlays.
    public static readonly string[] SharedRoles = ["song_info", "action_kusudama", "cut_in", "title_waiwai",
        "action_synchro_start", "action_solo_above_lane", "clear_effects"];

    // Waiwai gameplay (traced session11-waiwai): one voltage gauge and backdrop for both, the effect
    // overlays, and per lane the common movies with Waiwai's roll art, flights and synchro notes.
    private static readonly (string Archive, string Movie, int Index, int Depth)[] WaiwaiShared =
    [
        ("enso_system/common", "song_info", 13, 505),
        ("enso_system/common", "action_kusudama", 0, 500),
        ("enso_waiwai", "gage_w_normal", 14, 2002),
        ("waiwaicollabo/00_taiko", "donbg_w_00", 2, 3006),
        ("enso_waiwai_effect", "title_waiwai", 0, 505),
        ("enso_waiwai_effect", "cut_in", 0, 501),
        ("enso_waiwai_effect", "action_synchro_start", 0, 502),
        ("enso_waiwai_effect", "action_synchro_time", 0, 3000),
        ("enso_waiwai_effect", "action_solo_above_lane", 0, 502),
        ("enso_waiwai_effect", "action_solo_under_don", 0, 3002),
        ("enso_waiwai_effect", "clear_effects", 0, 502),
    ];

    // Per lane; index is the first lane's (the second takes the pair's next, except the listed ones).
    private static readonly (string Archive, string Movie, int? Index, int Depth)[] WaiwaiLane =
    [
        ("enso_system/common", "don3d", 26, 3001),
        ("waiwaicollabo/00_taiko", "renda_w_00", 10, 3005),
        ("enso_system/common", "lane", 14, 2003),
        ("enso_system/common", "lane_hit", 14, 2001),
        ("enso_system/common", "lane_syousetsu", null, 2000),
        ("enso_system/common", "onp_don", null, 1002),
        ("enso_system/common", "onp_katsu", null, 1002),
        ("enso_system/common", "onp_don_dai", null, 1002),
        ("enso_system/common", "onp_katsu_dai", null, 1002),
        ("enso_system/common", "onp_renda", null, 1002),
        ("enso_system/common", "onp_renda_dai", null, 1002),
        ("enso_system/common", "onp_fusen", null, 1002),
        ("enso_system/common", "onp_kusudama", null, 1002),
        ("enso_waiwai", "onp_synchro_don_1p", null, 1002),
        ("enso_waiwai", "onp_synchro_katsu_1p", null, 1002),
        ("enso_waiwai", "onp_synchro_daidon_1p", null, 1002),
        ("enso_waiwai", "onp_synchro_daikatsu_1p", null, 1002),
        ("waiwaicollabo/00_taiko", "onp_rare_0_w_00", null, 1002),
        ("enso_system/common", "lane_obi", 14, 507),
        ("indicator", "player_name", 11, 505),
        ("enso_system/don1p", "sinuchi_combo_bonus_don_1p", 20, 502),
        ("enso_system/common", "renda_num", 18, 502),
        ("enso_system/common", "lane_hit_effect", 14, 502),
        ("waiwaicollabo/00_taiko", "onp_kiseki_w_00_1p", 14, 502),
        ("enso_system/base1p", "action_fusen_1p", 0, 500),
    ];

    /// <summary>
    /// Waiwai gameplay for two players (traced session11-waiwai): both Dons side by side at the bottom
    /// (ensolayout 26 / 27), both lanes on the shared voltage gauge. The second lane's movies are the
    /// katsu2p / base2p / _2p variants under <see cref="PlayerTwoHostId"/>.
    /// </summary>
    // ponytail: collabo skin 00_taiko only (waiwaicollabo/NN_* per song are untraced).
    public static SceneDefinition CreateWaiwai(SceneId id, EnsoLayout layout)
    {
        IEnumerable<(SceneLayerDefinition Layer, int Depth)> lane(int lane) => WaiwaiLane.Select(entry =>
        {
            var index = entry.Index switch
            {
                null => (int?)null,
                10 => 10, // both lanes' roll art sit at 10 (traced)
                { } first => lane == 1 && (PairedIndices.Contains(first) || first == 26) ? first + 1 : first,
            };
            var at = index is { } i ? layout[i] : default;
            var (archive, movie) = (lane, entry.Archive) switch
            {
                (1, "enso_system/don1p") => ("enso_system/katsu2p", entry.Movie.Replace("_don_1p", "_katsu_2p")),
                (1, "enso_system/base1p") => ("enso_system/base2p", entry.Movie.Replace("_1p", "_2p")),
                (1, _) when entry.Movie.EndsWith("_1p", StringComparison.Ordinal) => (entry.Archive, entry.Movie[..^3] + "_2p"),
                _ => (entry.Archive, entry.Movie),
            };
            return (Layer: layer($"{archive}/packeddata.ddp", $"{movie}/{movie}.lm", at.X, at.Y,
                lane == 1 ? PlayerTwoHostId : StaticHostId), entry.Depth);
        });
        var shared = WaiwaiShared.Select(entry => (Layer: layer($"{entry.Archive}/packeddata.ddp", $"{entry.Movie}/{entry.Movie}.lm",
            layout[entry.Index].X, layout[entry.Index].Y, StaticHostId), entry.Depth));
        return new(SceneDefinition.CurrentVersion, id, shared.Concat(lane(0)).Concat(lane(1))
            .OrderByDescending(entry => entry.Depth)
            .Select(entry => entry.Layer));
    }

    // ensolayout pairs: the second player's lane movie sits at the next index (research/ensolayout.md,
    // traced session9-2p). Index 0 (overlays), 13 (song_info) and the backgrounds have no pair.
    private static readonly int[] PairedIndices = [2, 7, 9, 11, 14, 16, 18, 20, 22, 24];

    // enso_original part families and their variant counts (Green data). The game picks the parts
    // at every load (traced: same song, different mix); songs with a themed skin use that skin instead.
    // ponytail: independent random per part; bg_nomal/donbg may form sets (research/ensolayout.md).
    private static readonly (string Part, int Variants, string Suffix)[] OriginalParts =
    [
        ("bg_nomal", 5, ""), ("bg_fever", 4, ""), ("dance", 22, ""), ("dodai", 3, ""),
        ("fever", 4, ""), ("donbg", 6, "_1p"), ("chibi", 14, ""), ("renda", 3, ""),
    ];

    // Placement observed in the game (research/ensolayout.md): each movie sits at ensolayout[Index] and is
    // drawn by Depth, larger further back. Index null = the host positions it (notes, bar lines).
    // The game adds ensolayout[1] (-640,-360) to reach its centre-origin stage; ours is top-left.
    private static readonly (string Role, int Index, int Depth)[] SkinRoles =
    [
        ("bg_nomal", 4, 3500), ("bg_fever", 4, 3007), ("donbg", 2, 3006), ("renda", 9, 3005),
        ("dance", 5, 3004), ("dodai", 6, 3003), ("fever", 4, 3002), ("chibi", 7, 3000),
    ];

    private static readonly (string Archive, string Movie, int? Index, int Depth)[] SystemLayers =
    [
        ("enso_system/common", "don3d", 22, 3001),
        ("enso_system/common", "lane", 14, 2003),
        // One gauge per clear line; gameplay shows the course's one.
        ("enso_system/don1p", "gage_don_1p_easy", 14, 2002),
        ("enso_system/don1p", "gage_don_1p_normal", 14, 2002),
        ("enso_system/don1p", "gage_don_1p_hard", 14, 2002),
        ("enso_system/common", "lane_hit", 14, 2001),
        ("enso_system/common", "lane_syousetsu", null, 2000),
        // Note templates: the game gives each note its own depth from 1002 up.
        ("enso_system/common", "onp_don", null, 1002),
        ("enso_system/common", "onp_katsu", null, 1002),
        ("enso_system/common", "onp_don_dai", null, 1002),
        ("enso_system/common", "onp_katsu_dai", null, 1002),
        ("enso_system/common", "onp_renda", null, 1002),
        ("enso_system/common", "onp_renda_dai", null, 1002),
        ("enso_system/common", "onp_fusen", null, 1002),
        ("enso_system/common", "onp_kusudama", null, 1002),
        // Hand notes with feet: two-player gameplay only.
        ("enso_system/base1p", "onp_tetunagidon_1p", null, 1002),
        ("enso_system/base1p", "onp_tetunagikatsu_1p", null, 1002),
        ("enso_system/common", "action_result", 14, 1000),
        ("enso_system/common", "lane_obi", 14, 507),
        ("enso_system/base1p", "gage_fire_1p", 14, 506),
        ("enso_system/common", "song_info", 13, 505),
        ("indicator", "player_name", 11, 505),
        ("enso_system/don1p", "score_add_don_1p", 24, 502),
        ("enso_system/don1p", "combo_bonus_don_1p", 20, 502),
        ("enso_system/common", "renda_num", 18, 502),
        ("enso_system/common", "lane_hit_effect", 14, 502),
        ("enso_system/don1p", "onp_kiseki_don_1p", 14, 502),
        ("enso_system/base1p", "action_fusen_1p", 0, 500),
        ("enso_system/common", "action_kusudama", 0, 500),
        ("enso_system/base1p", "action_gogotime", 0, 500),
    ];

    /// <summary>Traced draw depth of a gameplay layer role (larger = further back); null if unknown.</summary>
    public static int? Depth(string role) =>
        SkinRoles.FirstOrDefault(entry => entry.Role == role) is { Role: not null } skin ? skin.Depth
        : SystemLayers.FirstOrDefault(entry => entry.Movie == role) is { Movie: not null } system ? system.Depth
        : WaiwaiShared.FirstOrDefault(entry => entry.Movie == role) is { Movie: not null } shared ? shared.Depth
        : WaiwaiLane.FirstOrDefault(entry => entry.Movie == role) is { Movie: not null } waiwai ? waiwai.Depth
        : null;

    /// <summary>
    /// Gameplay scene with either a themed skin (one archive holding every part, given with its
    /// movie list) or, without one, a random enso_original mix. <paramref name="side"/> 1 = the right
    /// drum's player alone: the same top lane, with Katsu-chan's katsu1p parts and the 2P Don
    /// backdrop (traced session8-p2-solo). <paramref name="twoPlayers"/>: a second lane below for
    /// Katsu-chan (katsu2p/base2p movies at the paired layout index, host <see cref="PlayerTwoHostId"/>);
    /// the lower half's backgrounds, dancers and Go-Go splash are not loaded (traced session9-2p).
    /// </summary>
    public static SceneDefinition Create(SceneId id, Random random, EnsoLayout layout, ThemedSkin? theme = null,
        int side = 0, bool twoPlayers = false)
    {
        // Random original parts are picked once; both lanes use the same variants (traced).
        var variants = OriginalParts.ToDictionary(entry => entry.Part, entry => random.Next(1, entry.Variants + 1));
        (SceneLayerDefinition Layer, int Depth)? skin(string role, int index, int depth, string sideSuffix, string host)
        {
            var at = layout[index];
            if (theme is not null)
            {
                // A part with per-side variants (the Don backdrop) takes the player's side.
                var movies = theme.Movies.Where(movie => Role(Path.GetFileNameWithoutExtension(movie)) == role).ToArray();
                var movie = movies.FirstOrDefault(movie => Path.GetFileNameWithoutExtension(movie).EndsWith(sideSuffix, StringComparison.Ordinal))
                    ?? movies.FirstOrDefault(movie => !Path.GetFileNameWithoutExtension(movie).EndsWith("_2p", StringComparison.Ordinal));
                return movie is null ? null : (layer(theme.Archive, movie, at.X, at.Y, host), depth);
            }
            var entry = OriginalParts.Single(entry => entry.Part == role);
            var suffix = entry.Suffix.Length == 0 ? "" : sideSuffix;
            var name = $"{role}_a_{variants[role]:00}{suffix}";
            return (layer($"enso_original/{name}/packeddata.ddp", $"{name}/{name}.lm", at.X, at.Y, host), depth);
        }
        // One player's lane: lane 1 = the second player below the first.
        IEnumerable<(SceneLayerDefinition Layer, int Depth)> lane(int lane, int side)
        {
            var host = lane == 1 ? PlayerTwoHostId : StaticHostId;
            int index(int at) => lane == 1 && PairedIndices.Contains(at) ? at + 1 : at;
            var skinLayers = SkinRoles
                .Where(entry => !twoPlayers || entry.Role is "donbg" or "chibi" or "renda")
                .Select(entry => skin(entry.Role, index(entry.Index), entry.Depth, side == 1 ? "_2p" : "_1p", host))
                .OfType<(SceneLayerDefinition Layer, int Depth)>();
            var systemLayers = SystemLayers
                .Where(entry => !(twoPlayers && entry.Movie == "action_gogotime"))
                .Where(entry => twoPlayers || !entry.Movie.StartsWith("onp_tetunagi", StringComparison.Ordinal))
                .Where(entry => lane == 0 || !SharedRoles.Contains(entry.Movie))
                .Select(entry =>
                {
                    var at = entry.Index is { } i ? layout[index(i)] : default;
                    var (archive, movie) = (lane, side, entry.Archive) switch
                    {
                        (1, _, "enso_system/don1p") => ("enso_system/katsu2p", entry.Movie.Replace("_don_1p", "_katsu_2p")),
                        (1, _, "enso_system/base1p") => ("enso_system/base2p", entry.Movie.Replace("_1p", "_2p")),
                        (0, 1, "enso_system/don1p") => ("enso_system/katsu1p", entry.Movie.Replace("_don_1p", "_katsu_1p")),
                        _ => (entry.Archive, entry.Movie),
                    };
                    return (Layer: layer($"{archive}/packeddata.ddp", $"{movie}/{movie}.lm", at.X, at.Y, host), entry.Depth);
                });
            return skinLayers.Concat(systemLayers);
        }
        var layers = (twoPlayers ? lane(0, 0).Concat(lane(1, 1)) : lane(0, side))
            .OrderByDescending(entry => entry.Depth) // stable: equal depths keep list order
            .Select(entry => entry.Layer);
        return new(SceneDefinition.CurrentVersion, id, layers);
    }

    /// <summary>Loads the title's stage offsets (config/common first, as Green reads them).</summary>
    public static EnsoLayout LoadLayout(string assetRoot)
    {
        var data = Path.GetFullPath(Path.Combine(assetRoot, "..", ".."));
        var path = new[] { Path.Combine(data, "config", "common", "ensolayout.bin"), Path.Combine(data, "ensolayout.bin") }
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"ensolayout.bin not found under {data}.");
        return EnsoLayout.Load(path);
    }

    /// <summary>
    /// song_info genre index (jpop, anime, vocaloid, doyo, variety, classic, game, namco) for a
    /// catalog category name.
    /// </summary>
    // ponytail: matched on the TJA library's English folder names; anything else shows as variety.
    public static int GenreIndex(string category)
    {
        var name = category.ToLowerInvariant();
        return name.Contains("pop") ? 0
            : name.Contains("anime") ? 1
            : name.Contains("vocaloid") ? 2
            : name.Contains("children") || name.Contains("folk") ? 3
            : name.Contains("classic") ? 5
            : name.Contains("game") ? 6
            : name.Contains("namco") ? 7
            : 4;
    }

    /// <summary>A themed gameplay skin: its archive and the movies it contains.</summary>
    public sealed record ThemedSkin(string Archive, IReadOnlyList<string> Movies);

    /// <summary>
    /// Gameplay role of a layer name: skin parts lose their variant ("bg_nomal_a_02", "donbg_b_01_2p" ->
    /// role), and Katsu-chan's and the second player's parts share Don-chan's first-player roles
    /// ("gage_katsu_1p_easy", "gage_katsu_2p_easy" -> "gage_don_1p_easy"; "gage_fire_2p" -> "gage_fire_1p").
    /// </summary>
    public static string Role(string layerName)
    {
        if (System.Text.RegularExpressions.Regex.Match(layerName, "^(.+?)_[ab]_[0-9]+(?:_1p|_2p|_common)?$") is { Success: true } match)
            return match.Groups[1].Value;
        // Synchro notes name the note (don / katsu), not the character.
        var name = layerName.StartsWith("onp_synchro_", StringComparison.Ordinal) ? layerName
            : layerName.Replace("_katsu_1p", "_don_1p", StringComparison.Ordinal)
                .Replace("_katsu_2p", "_don_1p", StringComparison.Ordinal);
        return name.EndsWith("_2p", StringComparison.Ordinal) ? name[..^3] + "_1p" : name;
    }

    private static SceneLayerDefinition layer(string archive, string movie, float x, float y, string host) => new(
        archive,
        movie,
        LumenMatrix.Identity with { X = x, Y = y },
        host);
}
