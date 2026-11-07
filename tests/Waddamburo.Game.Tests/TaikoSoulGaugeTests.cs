using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoSoulGaugeTests
{
    private static TaikoNoteJudgement judgement(TaikoHitResult result, bool strong = false) =>
        new(0, new(TimeSpan.Zero, PlayableNoteKind.Don), result, TimeSpan.Zero, strong);

    [Fact]
    public void GaugeFillsThroughClearToFullAndDrainsOnMisses()
    {
        // Normal, 5 stars, 100 notes: great +134, good +100, miss -133 (table row 100).
        var gauge = new TaikoSoulGauge(TaikoCourse.Normal, 5, 100);
        Assert.Equal(7000, gauge.Clear);
        Assert.True(gauge.Apply(judgement(TaikoHitResult.Great)));
        Assert.Equal(134, gauge.Value);
        Assert.False(gauge.Apply(judgement(TaikoHitResult.Great, strong: true)));
        gauge.Apply(judgement(TaikoHitResult.Good));
        Assert.Equal(234, gauge.Value);
        for (var i = 0; i < 51; i++) gauge.Apply(judgement(TaikoHitResult.Great));
        Assert.Equal(TaikoGaugeState.Cleared, gauge.State);
        Assert.Equal(35, gauge.FilledSegments);
        for (var i = 0; i < 30; i++) gauge.Apply(judgement(TaikoHitResult.Great));
        Assert.Equal(TaikoGaugeState.Full, gauge.State);
        Assert.Equal(TaikoSoulGauge.Segments, gauge.FilledSegments);
        gauge.Apply(judgement(TaikoHitResult.Miss));
        Assert.Equal(TaikoSoulGauge.Max - 133, gauge.Value);
        Assert.Equal(TaikoGaugeState.Cleared, gauge.State);
        for (var i = 0; i < 200; i++) gauge.Apply(judgement(TaikoHitResult.Miss));
        Assert.Equal(0, gauge.Value);
        Assert.False(gauge.Apply(judgement(TaikoHitResult.Miss)));
    }

    [Fact]
    public void CourseSelectsClearLineAndOniRates()
    {
        Assert.Equal(6000, new TaikoSoulGauge(TaikoCourse.Easy, 3, 50).Clear);
        var ura = new TaikoSoulGauge(TaikoCourse.Ura, null, 500);
        Assert.Equal(8000, ura.Clear);
        ura.Apply(judgement(TaikoHitResult.Great));
        Assert.Equal(26, ura.Value); // Oni 9-10 band, row 500
        var outside = new TaikoSoulGauge(TaikoCourse.Oni, 10, 3000);
        outside.Apply(judgement(TaikoHitResult.Great));
        Assert.Equal(10, outside.Value);
    }

    [Fact]
    public void DancersJoinPerQuarterOfTheClearLine()
    {
        // Hard (clear 7000): one dancer, then one more every 1750 points (trace-confirmed).
        var gauge = new TaikoSoulGauge(TaikoCourse.Hard, 5, 100);
        var counts = new List<int> { TaikoSkinPresentation.DancerCount(gauge) };
        while (gauge.State != TaikoGaugeState.Full)
        {
            gauge.Apply(judgement(TaikoHitResult.Great));
            counts.Add(TaikoSkinPresentation.DancerCount(gauge));
        }
        Assert.Equal([1, 2, 3, 4, 5, 6], counts.Distinct());
        Assert.Equal(1 + TaikoSoulGauge.Max * 4 / 7000, TaikoSkinPresentation.DancerCount(gauge));
    }

    [Fact]
    public void BranchedRouteGainsScaleByNormalComboOverRouteCombo()
    {
        // Normal: 100 notes. Master: the same 99 plus 4 of its own = 103.
        static TimeSpan s(double seconds) => TimeSpan.FromSeconds(seconds);
        var notes = Enumerable.Range(0, 99).Select(i => new PlayableHitObject(s(i * 0.1), PlayableNoteKind.Don))
            .Append(new PlayableHitObject(s(20), PlayableNoteKind.Don) { Branch = ChartBranch.Normal })
            .Concat(Enumerable.Range(0, 4).Select(i => new PlayableHitObject(s(20 + i * 0.1), PlayableNoteKind.Don) { Branch = ChartBranch.Master }));
        var chart = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"), TimeSpan.Zero, s(30),
            notes, [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)], [new ChartScrollPoint(TimeSpan.Zero, 1)],
            [new ChartEffectPoint(TimeSpan.Zero, false)], [])
        {
            Level = 5,
            Branching = new ChartBranching([new ChartBranchCheck(s(18), s(19.5), new(1, 2), new(1, 2), new(1, 2))],
                ChartBranchPoints.Default),
        };
        var gauge = new TaikoSoulGauge(TaikoCourse.Normal, chart);
        for (var i = 0; i < 103; i++)
            gauge.Apply(new(0, new(s(20), PlayableNoteKind.Don) { Branch = ChartBranch.Master }, TaikoHitResult.Great, TimeSpan.Zero, false));
        Assert.Equal(TaikoSoulGauge.Max, gauge.Value);
        var exact = new TaikoSoulGauge(TaikoCourse.Normal, chart);
        for (var i = 0; i < 52; i++)
            exact.Apply(new(0, new(s(20), PlayableNoteKind.Don) { Branch = ChartBranch.Master }, TaikoHitResult.Great, TimeSpan.Zero, false));
        Assert.Equal((int)(52 * 134 * 100.0 / 103), exact.Value); // 6765: below clear, normal would be 6968
    }

    [Fact]
    public void OsuChartsFillAsOniWhateverTheirSlot()
    {
        var chart = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.OsuLazer, "set"), "map"), TimeSpan.Zero,
            TimeSpan.FromSeconds(60), Enumerable.Range(0, 500).Select(i => new PlayableHitObject(TimeSpan.FromSeconds(i * 0.1), PlayableNoteKind.Don)),
            [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)], [new ChartScrollPoint(TimeSpan.Zero, 1)],
            [new ChartEffectPoint(TimeSpan.Zero, false)], []) { Level = 5 };
        var easySlot = new TaikoSoulGauge(TaikoCourse.Easy, chart);
        var oni = new TaikoSoulGauge(TaikoCourse.Oni, 5, 500);
        Assert.Equal(8000, easySlot.Clear);
        easySlot.Apply(judgement(TaikoHitResult.Great));
        oni.Apply(judgement(TaikoHitResult.Great));
        Assert.Equal(oni.Value, easySlot.Value);
    }
}
