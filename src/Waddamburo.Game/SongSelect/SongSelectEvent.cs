using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.SongSelect;

/// <summary>
/// TaikOnline's running event (GET api/wdb/event, one of Green's collab campaigns in rotation): its name, period and
/// songs (stock musicids). Song Select lists them in an event folder; their card plays earn the event's tickets.
/// <paramref name="Art"/>: Green's own feature folder art for it (the featureboard NUTs' texture index), or null.
/// </summary>
public sealed record SongSelectEvent(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, List<string> MusicIds,
    int? Art = null)
{
    /// <summary>A part of Green's feature folder art as a native fill: v (spine), h (header), icon, image (open folder).</summary>
    public static LumenNativeSurfaceKey FeatureBoard(string part, int art) => new($"featureboard:{part}:{art}");
}
