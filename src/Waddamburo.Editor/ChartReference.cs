using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public readonly record struct ReferenceNote(double Beat, double EndBeat, TimeSpan Time, string Label, bool Ka, bool Strong, bool IsLong);
public readonly record struct ReferenceMeasure(double Beat, int Number, bool Visible);
public readonly record struct ReferenceTiming(double Beat, string Label);

/// <summary>Chart notes and timing projected onto the same beat map used by modchart keyframes.</summary>
public sealed class ChartReference
{
    public ImmutableArray<ReferenceNote> Notes { get; }
    public ImmutableArray<double> SnapBeats { get; }
    public ImmutableArray<ReferenceMeasure> Measures { get; }
    public ImmutableArray<ReferenceTiming> Timing { get; }
    public ChartBranch Branch { get; }
    public bool HasBranches { get; }
    private readonly ImmutableArray<ChartTimingPoint> _timing;

    public ChartReference(PlayableChart chart, ChartEffects beatMap, ChartBranch branch = ChartBranch.Normal)
    {
        Branch = branch;
        HasBranches = chart.Branching is not null || chart.HitObjects.Any(n => n.Branch.HasValue) || chart.LongNotes.Any(n => n.Branch.HasValue);
        Notes = [.. chart.HitObjects.Where(n => PlayableChart.IsOn(n.Branch, branch)).Select(n =>
                new ReferenceNote(beatMap.BeatAt(n.StartTime), beatMap.BeatAt(n.StartTime), n.StartTime, n.Kind.ToString(),
                    n.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa, n.IsStrong, false))
            .Concat(chart.LongNotes.Where(n => PlayableChart.IsOn(n.Branch, branch)).Select(n =>
                new ReferenceNote(beatMap.BeatAt(n.StartTime), beatMap.BeatAt(n.EndTime), n.StartTime, n.Kind.ToString(), false,
                    n.Kind == PlayableLongNoteKind.BigRoll, true))).OrderBy(n => n.Beat)];
        SnapBeats = [.. Notes.SelectMany(n => n.IsLong ? new[] { n.Beat, n.EndBeat } : [n.Beat]).Distinct().Order()];
        var bars = chart.BarLinesOn(branch).GroupBy(b => b.Time).Select(g => g.First()).OrderBy(b => b.Time).ToArray();
        var timing = chart.RouteControls.IsEmpty ? chart.TimingPoints : chart.RouteControls[(int)branch].Timing;
        _timing = timing;
        if (bars.Length > 0)
            Measures = [.. bars.Select((b, i) => new ReferenceMeasure(beatMap.BeatAt(b.Time), i + 1, b.IsVisible))];
        else
        {
            var measures = new List<ReferenceMeasure>();
            var beat = 0d;
            var end = beatMap.BeatAt(chart.Duration);
            while (beat <= end && measures.Count < 100_000)
            {
                measures.Add(new(beat, measures.Count + 1, true));
                var point = timing.LastOrDefault(t => t.Time <= beatMap.TimeAtBeat(beat), timing[0]);
                beat += point.BeatsPerMeasure * 4d / point.BeatUnit;
            }
            Measures = [.. measures];
        }
        var marks = new List<ReferenceTiming>();
        ChartTimingPoint? previous = null;
        foreach (var point in timing)
        {
            var bpm = previous is null || previous.Value.BeatsPerMinute != point.BeatsPerMinute;
            var meter = previous is null || previous.Value.BeatsPerMeasure != point.BeatsPerMeasure || previous.Value.BeatUnit != point.BeatUnit;
            if (bpm || meter) marks.Add(new(beatMap.BeatAt(point.Time),
                string.Join(" | ", new[] { bpm ? $"{point.BeatsPerMinute:0.###} BPM" : null,
                    meter ? $"{point.BeatsPerMeasure}/{point.BeatUnit}" : null }.Where(s => s is not null))));
            previous = point;
        }
        for (var i = 0; i + 1 < beatMap.Beats.Length; i++)
            if (beatMap.Beats[i].BeatsPerSecond == 0)
                marks.Add(new(beatMap.Beats[i].Beat, $"Delay {(beatMap.Beats[i + 1].Time - beatMap.Beats[i].Time).TotalSeconds:0.###} s"));
        if (chart.Branching is { } branching)
            marks.AddRange(branching.Checks.Select(c => new ReferenceTiming(beatMap.BeatAt(c.Start), "Branch")));
        Timing = [.. marks.OrderBy(m => m.Beat)];
    }

    public double Snap(double beat)
    {
        if (SnapBeats.IsEmpty) return beat;
        var index = SnapBeats.BinarySearch(beat);
        if (index >= 0) return SnapBeats[index];
        index = ~index;
        if (index == 0) return SnapBeats[0];
        if (index == SnapBeats.Length) return SnapBeats[^1];
        return beat - SnapBeats[index - 1] <= SnapBeats[index] - beat ? SnapBeats[index - 1] : SnapBeats[index];
    }

    public ChartTimingPoint TimingAt(TimeSpan time) => _timing.LastOrDefault(p => p.Time <= time, _timing[0]);
    public ReferenceMeasure? MeasureAt(double beat) => Measures.Where(m => m.Beat <= beat).Select(m => (ReferenceMeasure?)m).LastOrDefault();
}
