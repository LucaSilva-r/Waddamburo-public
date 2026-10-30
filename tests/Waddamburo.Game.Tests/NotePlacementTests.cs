using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class NotePlacementTests
{
    [Fact]
    public void ControlPointsAreFoundByTimeTheLastOfATimeWinning()
    {
        var points = new[] { new ChartTimingPoint(TimeSpan.Zero, 100, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(5), 150, 4, 4),
            new ChartTimingPoint(TimeSpan.FromSeconds(5), 200, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(9), 300, 4, 4) };
        var scrolls = new[] { new ChartScrollPoint(TimeSpan.Zero, 1), new ChartScrollPoint(TimeSpan.FromSeconds(1), 2), new ChartScrollPoint(TimeSpan.FromSeconds(3), 3) };
        var test = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Stock, "a"), "m"), TimeSpan.Zero, TimeSpan.FromSeconds(10),
            [new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don)], points, scrolls,
            [new ChartEffectPoint(TimeSpan.Zero, false)], [new ChartBarLine(TimeSpan.Zero, true)]);

        Assert.Equal(100, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(4)));
        Assert.Equal(200, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(300, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, test.ScrollAt(TimeSpan.Zero));
        Assert.Equal(2, test.ScrollAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, test.ScrollAt(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ANoteIsVisibleFromTheRightEdgeUntilPastTheLeft()
    {
        var (from, until) = TaikoLumenPresentation.VisibleWindow(TimeSpan.FromSeconds(100), 400, 300);

        Assert.Equal(TimeSpan.FromSeconds(100 - 2.7), from);
        Assert.Equal(TimeSpan.FromSeconds(101), until);
    }

    [Fact]
    public void ANoteThatBarelyMovesIsAlwaysVisible()
    {
        Assert.Equal((TimeSpan.MinValue, TimeSpan.MaxValue), TaikoLumenPresentation.VisibleWindow(TimeSpan.Zero, -50, 300));
    }
}
