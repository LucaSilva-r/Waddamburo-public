using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Skins.Green.Art;

/// <summary>
/// The reward shop's season art: nutdata/S11100-1/appendable/00/season_(BG|thema|icon|daishi_1p2p|medal_1p2p), one
/// texture per season (the 1p2p ones two per season: 1P, 2P).
/// </summary>
public sealed class SeasonArt(NutData nutData)
{
    /// <summary>A season's art; <paramref name="side"/> picks the 1P/2P one of the daishi and medal NUTs.</summary>
    public static LumenNativeSurfaceKey Key(string kind, int season, int side = 0) => new($"season:{kind}:{season}:{side}");

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("season:", StringComparison.Ordinal))
            return null;
        var parts = key.Value.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var season) || !int.TryParse(parts[3], out var side))
            return null;
        var path = $"S11100-1/appendable/00/season_{parts[1]}/season_{parts[1]}.nut";
        // ponytail: index = season id (1-4); the 5 textures' first is taken as season 0 until seen otherwise.
        var index = parts[1].EndsWith("1p2p", StringComparison.Ordinal) ? season * 2 + side : season;
        return index < nutData.Textures(path).Length ? nutData.Texture(path, index) : null;
    }
}
