using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Dan;

/// <summary>One song's place in a course's timeline: its next-song card starts at Start, its chart's time zero is
/// Zero, and End is where the next song's card starts.</summary>
public sealed record DanTimelineSong(TimeSpan Start, TimeSpan Zero, TimeSpan End, PlayableChart Chart, TaikoCourse Course);

/// <summary>
/// A course's songs as one chart with the next-song card's time between them, so score, combo, gauge and the
/// replay simply carry across them (traced session30: combo goes on, each song keeps its own score and gauge rates).
/// </summary>
public sealed class DanCourseTimeline
{
    /// <summary>Traced session30: from the card's start to the song's first note, about 6.3 s.</summary>
    public static readonly TimeSpan CardLeadIn = TimeSpan.FromSeconds(6);

    private DanCourseTimeline(PlayableChart merged, IReadOnlyList<DanTimelineSong> songs) => (Chart, Songs) = (merged, songs);

    public PlayableChart Chart { get; }

    public IReadOnlyList<DanTimelineSong> Songs { get; }

    // ponytail: a branched song plays its normal route; the merged chart would need each song's branch checks.
    public static DanCourseTimeline Merge(IReadOnlyList<(PlayableChart Chart, TaikoCourse Course)> charts)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentOutOfRangeException.ThrowIfZero(charts.Count);
        var songs = new List<DanTimelineSong>();
        var at = TimeSpan.Zero;
        foreach (var (authored, course) in charts)
        {
            var chart = authored.OnRoute(ChartBranch.Normal);
            var leadIn = new GameplayTimeline(chart.AuthoredOffset, CardLeadIn).LeadIn;
            songs.Add(new DanTimelineSong(at, at + leadIn, at + leadIn + chart.Duration, chart, course));
            at += leadIn + chart.Duration;
        }
        // Control points must start at time zero: the first song's first ones also cover its card.
        var first = songs[0].Chart;
        return new DanCourseTimeline(new PlayableChart(first.Key, TimeSpan.Zero, at,
            songs.SelectMany(static song => song.Chart.HitObjects.Select(note => note.At(note.StartTime + song.Zero))),
            startAtZero(songs.SelectMany(static song => song.Chart.TimingPoints.Select(point => point.At(point.Time + song.Zero))), first.TimingPoints[0]),
            startAtZero(songs.SelectMany(static song => song.Chart.ScrollPoints.Select(point => point.At(point.Time + song.Zero))), first.ScrollPoints[0]),
            startAtZero(songs.SelectMany(static song => song.Chart.EffectPoints.Select(point => point.At(point.Time + song.Zero))), first.EffectPoints[0]),
            songs.SelectMany(static song => song.Chart.BarLines.Select(line => line.At(line.Time + song.Zero))),
            songs.SelectMany(static song => song.Chart.LongNotes.Select(note => note.At(note.StartTime + song.Zero, note.EndTime + song.Zero))))
        {
            Segments = [.. songs.Select(static song => new ChartSegment(song.Zero, song.Course, song.Chart))],
        }, songs);
    }

    private static IEnumerable<T> startAtZero<T>(IEnumerable<T> points, T first) => [first, .. points];
}
