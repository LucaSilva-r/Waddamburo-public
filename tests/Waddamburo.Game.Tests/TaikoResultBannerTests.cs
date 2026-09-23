using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoResultBannerTests
{
    [Fact]
    public void FiresOneSecondIntoTheMeasureHoldingTheLastNote()
    {
        // Bars every 2 s; the last note sits inside the measure starting at 4 s.
        var chart = chartWith([seconds(1), seconds(4.5)], [seconds(0), seconds(2), seconds(4), seconds(6)]);

        Assert.Equal(seconds(5), TaikoResultBanner.Time(chart));
    }

    [Fact]
    public void LastNoteOnABarLineUsesThatMeasure()
    {
        var chart = chartWith([seconds(1), seconds(4)], [seconds(0), seconds(2), seconds(4), seconds(6)]);

        Assert.Equal(seconds(5), TaikoResultBanner.Time(chart));
    }

    [Theory]
    [InlineData(false, false, "fail")]
    [InlineData(false, true, "fail")]
    [InlineData(true, false, "success")]
    [InlineData(true, true, "fullcombo")]
    public void PicksTheMovieLabel(bool cleared, bool fullCombo, string label) =>
        Assert.Equal(label, TaikoResultBanner.Label(cleared, fullCombo));

    private static TimeSpan seconds(double value) => TimeSpan.FromSeconds(value);

    private static PlayableChart chartWith(TimeSpan[] notes, TimeSpan[] bars) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        seconds(8),
        notes.Select(time => new PlayableHitObject(time, PlayableNoteKind.Don)),
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        bars.Select(time => new ChartBarLine(time, true)));
}
