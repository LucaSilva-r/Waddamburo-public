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
}
