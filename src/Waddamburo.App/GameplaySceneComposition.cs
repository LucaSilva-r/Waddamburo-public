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
        : null;

    /// <summary>
    /// Gameplay scene with either a themed skin (one archive holding every part, given with its
    /// movie list) or, without one, a random enso_original mix. <paramref name="side"/> 1 = the right
    /// drum's player alone: the same top lane, with Katsu-chan's katsu1p parts and the 2P Don
    /// backdrop (traced session8-p2-solo).
    /// </summary>
    public static SceneDefinition Create(SceneId id, Random random, EnsoLayout layout, ThemedSkin? theme = null, int side = 0)
    {
        var sideSuffix = side == 1 ? "_2p" : "_1p";
        (SceneLayerDefinition Layer, int Depth)? skin(string role, int index, int depth)
        {
            var at = layout[index];
            if (theme is not null)
            {
                // A part with per-side variants (the Don backdrop) takes the player's side.
                var movies = theme.Movies.Where(movie => Role(Path.GetFileNameWithoutExtension(movie)) == role).ToArray();
                var movie = movies.FirstOrDefault(movie => Path.GetFileNameWithoutExtension(movie).EndsWith(sideSuffix))
                    ?? movies.FirstOrDefault(movie => !Path.GetFileNameWithoutExtension(movie).EndsWith("_2p"));
                return movie is null ? null : (layer(theme.Archive, movie, at.X, at.Y), depth);
            }
            var entry = OriginalParts.Single(entry => entry.Part == role);
            var suffix = entry.Suffix.Length == 0 ? "" : sideSuffix;
            var name = $"{role}_a_{random.Next(1, entry.Variants + 1):00}{suffix}";
            return (layer($"enso_original/{name}/packeddata.ddp", $"{name}/{name}.lm", at.X, at.Y), depth);
        }
        var layers = SkinRoles
            .Select(entry => skin(entry.Role, entry.Index, entry.Depth))
            .OfType<(SceneLayerDefinition Layer, int Depth)>()
            .Concat(SystemLayers.Select(entry =>
            {
                var at = entry.Index is { } index ? layout[index] : default;
                var (archive, movie) = side == 1 && entry.Archive == "enso_system/don1p"
                    ? ("enso_system/katsu1p", entry.Movie.Replace("_don_1p", "_katsu_1p"))
                    : (entry.Archive, entry.Movie);
                return (Layer: layer($"{archive}/packeddata.ddp", $"{movie}/{movie}.lm", at.X, at.Y), entry.Depth);
            }))
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
    /// role), and Katsu-chan's player parts share Don-chan's roles ("gage_katsu_1p_easy" -> "gage_don_1p_easy").
    /// </summary>
    public static string Role(string layerName)
    {
        var name = layerName.Replace("_katsu_1p", "_don_1p", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Match(name, "^(.+?)_[ab]_[0-9]+(?:_1p|_2p|_common)?$") is { Success: true } match
            ? match.Groups[1].Value
            : name;
    }

    private static SceneLayerDefinition layer(string archive, string movie, float x, float y) => new(
        archive,
        movie,
        LumenMatrix.Identity with { X = x, Y = y },
        StaticHostId);
}
