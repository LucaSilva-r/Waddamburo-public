using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoResultBannerTests
{
    [Fact]
    public void FiresAfterTheLastNote()
    {
        var chart = chartWith([seconds(1), seconds(4.5)], [seconds(0), seconds(2), seconds(4), seconds(6)]);

        Assert.Equal(seconds(4.5) + TaikoResultBanner.Delay, TaikoResultBanner.Time(chart));
    }

    [Fact]
    public void ARollEndingLaterCountsByItsEnd()
    {
        var chart = chartWith([seconds(1)], [seconds(0), seconds(2), seconds(4), seconds(6)],
            [new PlayableLongNote(seconds(3), seconds(6.5), PlayableLongNoteKind.Balloon, 5)]);

        Assert.Equal(seconds(6.5) + TaikoResultBanner.Delay, TaikoResultBanner.Time(chart));
    }

    [Theory]
    [InlineData(false, false, "fail")]
    [InlineData(false, true, "fail")]
    [InlineData(true, false, "success")]
    [InlineData(true, true, "fullcombo")]
    public void PicksTheMovieLabel(bool cleared, bool fullCombo, string label) =>
        Assert.Equal(label, TaikoResultBanner.Label(cleared, fullCombo));

    private static TimeSpan seconds(double value) => TimeSpan.FromSeconds(value);

    private static PlayableChart chartWith(TimeSpan[] notes, TimeSpan[] bars, PlayableLongNote[]? longNotes = null) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        seconds(8),
        notes.Select(time => new PlayableHitObject(time, PlayableNoteKind.Don)),
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        bars.Select(time => new ChartBarLine(time, true)),
        longNotes);
}
