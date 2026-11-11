namespace Waddamburo.Editor;

/// <summary>Timeline headers keep their beat alignment when a track's curve is expanded below it.</summary>
public readonly record struct TimelineTrackRow(string Track, float Top, float HeaderHeight, float GraphHeight)
{
    public float Height => HeaderHeight + GraphHeight;
    public float KeyCenter => Top + HeaderHeight / 2;
    public bool ContainsHeader(float y) => y >= Top && y < Top + HeaderHeight;

    public static IReadOnlyList<TimelineTrackRow> Arrange(IEnumerable<string> tracks, IReadOnlySet<string> expanded,
        float headerHeight = 32, float graphHeight = 156)
    {
        var rows = new List<TimelineTrackRow>();
        var y = 0f;
        foreach (var track in tracks)
        {
            var row = new TimelineTrackRow(track, y, headerHeight, expanded.Contains(track) ? graphHeight : 0);
            rows.Add(row); y += row.Height;
        }
        return rows;
    }
}
