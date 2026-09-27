using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The onp_moji label under each note (traced session17-note-text): the game reads it from the fumen
/// note type (1 don, 2 do, 3 ko, 4 katsu, 5 ka, big don_dai / katsu_dai, hand notes too). Charts
/// without authored runs (TJA) get them the way tja2fumen assigns fumen types: cluster close notes,
/// then do/ka inside a cluster, ko on every other don of an odd all-don run, the last note full.
/// Clustering reimplemented from tja2fumen (MIT, <c>fix_dk_note_types</c>).
/// </summary>
public static class TaikoNoteText
{
    /// <summary>Text sits this far below its note (traced y 339 vs 257).</summary>
    public const float Offset = 82;

    public static string Balloon(PlayableLongNoteKind kind) => kind == PlayableLongNoteKind.Kusudama ? "imo" : "geki_renda";

    public static string[] Labels(PlayableChart chart)
    {
        var notes = chart.HitObjects;
        // ponytail: "any run note" means the chart authored its syllables (fumen); a fumen without
        // any run gets clustered too, which reproduces what tja2fumen would have written.
        if (notes.Any(note => note.InRun))
            return [.. notes.Select(note => label(note, note.InRun, note.IsKo))];
        var shortForm = new bool[notes.Length];
        var ko = new bool[notes.Length];
        foreach (var cluster in Clusters(chart))
        {
            foreach (var index in cluster) shortForm[index] = !notes[index].IsStrong;
            if (cluster.Count % 2 == 1 && cluster.All(index => notes[index].Kind == PlayableNoteKind.Don))
                for (var i = 1; i < cluster.Count; i += 2) ko[cluster[i]] = true;
            if (!(cluster.Count == 4 && fastRun(chart, cluster)))
                shortForm[cluster[^1]] = ko[cluster[^1]] = false;
        }
        return [.. notes.Select((note, index) => label(note, shortForm[index], ko[index]))];
    }

    private static string label(PlayableHitObject note, bool shortForm, bool ko) => note.Kind switch
    {
        PlayableNoteKind.Don => ko ? "ko" : shortForm ? "do" : "don",
        PlayableNoteKind.Ka => shortForm ? "ka" : "katsu",
        PlayableNoteKind.BigDon => "don_dai",
        _ => "katsu_dai",
    };

    private static bool fastRun(PlayableChart chart, List<int> cluster) =>
        cluster.Take(3).All(index => diff(chart, index) < eighth(chart));

    // Whole ms to the next note (0 for the last one, as in tja2fumen).
    private static int diff(PlayableChart chart, int index) => index + 1 < chart.HitObjects.Length
        ? (int)(chart.HitObjects[index + 1].StartTime - chart.HitObjects[index].StartTime).TotalMilliseconds : 0;

    private static double songBpm(PlayableChart chart)
    {
        // The most common measure BPM, ties to the lowest.
        double bpmAt(TimeSpan time) => chart.TimingPoints.LastOrDefault(point => point.Time <= time, chart.TimingPoints[0]).BeatsPerMinute;
        var bpms = chart.BarLines.IsEmpty ? [chart.TimingPoints[0].BeatsPerMinute] : chart.BarLines.Select(bar => bpmAt(bar.Time)).ToArray();
        return bpms.GroupBy(bpm => bpm).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;
    }

    private static int eighth(PlayableChart chart) => (int)(240_000 / songBpm(chart) / 8);

    internal static List<List<int>> Clusters(PlayableChart chart)
    {
        var count = chart.HitObjects.Length;
        var diffs = Enumerable.Range(0, count).Select(index => diff(chart, index)).ToArray();
        var quarter = (int)(240_000 / songBpm(chart) / 4);
        var eighthDuration = eighth(chart);
        var under = diffs.Distinct().Where(value => value < quarter).Order().ToArray();
        // Anything faster than an 8th is one stream; each slower spacing is clustered on its own after it.
        var groups = new List<int[]>();
        if (under.Any(value => value < eighthDuration)) groups.Add([.. under.Where(value => value < eighthDuration)]);
        groups.AddRange(under.Where(value => value >= eighthDuration).Select(value => new[] { value }));
        var items = Enumerable.Range(0, count).Select(index => (Notes: new List<int> { index }, Cluster: false)).ToList();
        foreach (var group in groups)
        {
            var result = new List<(List<int> Notes, bool Cluster)>();
            List<int>? current = null;
            foreach (var item in items)
            {
                if (item.Cluster)
                {
                    if (current is not null) { result.Add((current, true)); current = null; }
                    result.Add(item);
                }
                else if (group.Contains(diffs[item.Notes[0]]))
                    (current ??= []).Add(item.Notes[0]);
                else if (current is not null)
                {
                    current.Add(item.Notes[0]);
                    result.Add((current, true));
                    current = null;
                }
                else
                    result.Add(item);
            }
            if (current is not null) result.Add((current, true));
            items = result;
        }
        return [.. items.Select(item => item.Notes)];
    }
}
