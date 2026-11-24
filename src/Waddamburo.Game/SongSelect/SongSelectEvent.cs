using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.SongSelect;

/// <summary>
/// TaikOnline's running event (GET api/wdb/event, one of Green's collab campaigns in rotation): its name, period and
/// songs (stock musicids). Song Select lists them in an event folder; their card plays earn the event's tickets.
/// <paramref name="Art"/>: the game's own feature folder art for it, "release:index" (a release's featureboard NUTs in Green's
/// nutdata, e.g. "S11100-1:6" Green's 東方Project, "ST8100-1:9" Red's パズドラ), or null.
/// </summary>
public sealed record SongSelectEvent(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, List<string> MusicIds,
    string? Art = null)
{
    /// <summary>A part of the feature folder art as a native fill: v (spine), h (header), icon, image (open folder).</summary>
    public static LumenNativeSurfaceKey FeatureBoard(string part, string art) => new($"featureboard:{part}:{art}");
}
