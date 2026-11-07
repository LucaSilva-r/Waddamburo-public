using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class ChartReferenceTests
{
    private static readonly double[] ExpertBeats = [1, 6.5];
    private static PlayableChart Source() => new(new(new(SongSourceKind.Tja, "reference"), "oni"), TimeSpan.Zero,
        TimeSpan.FromSeconds(12),
        [new(TimeSpan.FromSeconds(.5), PlayableNoteKind.Don),
            new(TimeSpan.FromSeconds(4), PlayableNoteKind.Ka) { Branch = ChartBranch.Normal },
            new(TimeSpan.FromSeconds(4.25), PlayableNoteKind.BigDon) { Branch = ChartBranch.Expert },
            new(TimeSpan.FromSeconds(4.5), PlayableNoteKind.BigKa) { Branch = ChartBranch.Master }],
        [new(TimeSpan.Zero, 120, 4, 4), new(TimeSpan.FromSeconds(5), 240, 3, 4)],
        [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)],
        [new(TimeSpan.Zero, true), new(TimeSpan.FromSeconds(2), false), new(TimeSpan.FromSeconds(5), true)],
        [new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), PlayableLongNoteKind.Roll) { Branch = ChartBranch.Normal }])
    {
        Effects = new() { Beats = [new(TimeSpan.Zero, 0, 2), new(TimeSpan.FromSeconds(2), 4, 0),
            new(TimeSpan.FromSeconds(3), 4, 2), new(TimeSpan.FromSeconds(5), 8, 4)] },
    };

    [Fact]
    public void BranchNotesAndLongEndsSnapToTheirActualTimesOnTheModchartBeatMap()
    {
        var chart = Source(); var normal = new ChartReference(chart, chart.Effects);
        Assert.True(normal.HasBranches);
        Assert.Equal(new[] { 1d, 6, 8, 12 }, normal.SnapBeats);
        Assert.Equal(6, normal.Snap(6.2));
        Assert.Equal(8, normal.Snap(10)); // tie goes earlier
        var expert = new ChartReference(chart, chart.Effects, ChartBranch.Expert);
        Assert.Equal(ExpertBeats, expert.SnapBeats);
        Assert.Equal(6.5, expert.Snap(6.2));
        Assert.Equal(PlayableNoteKind.BigDon.ToString(), expert.Notes[1].Label);
        Assert.True(expert.Notes[1].Strong);
        var master = new ChartReference(chart, chart.Effects, ChartBranch.Master);
        Assert.Equal(7, master.Snap(6.2)); Assert.True(master.Notes[1].Ka);
    }

    [Fact]
    public void HiddenBarsStillCountAsMeasuresAndBpmMeterAndDelayChangesAreVisible()
    {
        var chart = Source(); var reference = new ChartReference(chart, chart.Effects);
        Assert.Equal(new[] { 0d, 4, 8 }, reference.Measures.Select(m => m.Beat));
        Assert.Equal(2, reference.Measures[1].Number); Assert.False(reference.Measures[1].Visible);
        Assert.Contains(reference.Timing, m => m.Beat == 8 && m.Label.Contains("240 BPM") && m.Label.Contains("3/4"));
        Assert.Contains(reference.Timing, m => m.Beat == 4 && m.Label.Contains("Delay 1 s"));
    }

    [Fact]
    public void RouteSpecificTempoAndBarsKeepTheSharedKeyframeCoordinateSystem()
    {
        var chart = Source() with { RouteControls = [new(Source().TimingPoints, []),
            new([new(TimeSpan.Zero, 90, 4, 4), new(TimeSpan.FromSeconds(4), 180, 3, 4)], [])
                { Bars = [new(TimeSpan.Zero, true), new(TimeSpan.FromSeconds(4), true)] }, new(Source().TimingPoints, [])] };
        var reference = new ChartReference(chart, chart.Effects, ChartBranch.Expert);
        Assert.Equal(6, reference.Measures[1].Beat);
        Assert.Contains(reference.Timing, m => m.Beat == 6 && m.Label.Contains("180 BPM"));
        Assert.Equal(6.5, reference.Notes[1].Beat);
        Assert.Equal(180, reference.TimingAt(TimeSpan.FromSeconds(4.5)).BeatsPerMinute);
        Assert.Equal(2, reference.MeasureAt(6.5)?.Number);
    }

    [Fact]
    public void ChartsWithoutBarsGetMeterAwareMeasuresAndEmptyNoteSetsDoNotMoveThePointer()
    {
        var chart = new PlayableChart(new(new(SongSourceKind.Tja, "empty"), "oni"), TimeSpan.Zero, TimeSpan.FromSeconds(8), [],
            [new(TimeSpan.Zero, 120, 4, 4), new(TimeSpan.FromSeconds(2), 120, 3, 4)], [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], []);
        var reference = new ChartReference(chart, new EffectPreview(chart).BeatMap);
        Assert.Equal(new[] { 0d, 4, 7, 10, 13, 16 }, reference.Measures.Select(m => m.Beat));
        Assert.Equal(3.125, reference.Snap(3.125));
    }
}
