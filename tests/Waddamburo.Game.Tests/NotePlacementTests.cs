using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class NotePlacementTests
{
    [Theory]
    [InlineData(60, 1, 237)]
    [InlineData(120, 1, 474)]
    [InlineData(180, 1, 711)]
    [InlineData(240, 1, 948)]
    [InlineData(120, 0.5, 237)]
    [InlineData(120, 2, 948)]
    [InlineData(120, 1.1, 521.4)]
    [InlineData(120, 0, 0)]
    [InlineData(120, -1, -474)]
    public void ScrollSpeedUsesTheMeasuredFourBeatDistance(double bpm, double scroll, double expected)
    {
        Assert.Equal(expected, NoteGeometry.ScrollPixelsPerSecond(bpm, scroll), 8);
    }

    [Theory]
    [InlineData(120, 4, 4, 948)]
    [InlineData(180, 4, 4, 948)]
    [InlineData(120, 3, 4, 711)]
    [InlineData(120, 6, 8, 711)]
    public void MeasureSpacingFollowsItsDurationWithoutRescalingVelocity(double bpm, int numerator, int denominator, double expected)
    {
        var duration = 60 / bpm * numerator * 4 / denominator;

        Assert.Equal(expected, duration * NoteGeometry.ScrollPixelsPerSecond(bpm, 1), 8);
    }

    [Fact]
    public void FourBeatsBeginOffscreenAndVisibilityUsesTheCorrectedVelocity()
    {
        const float hitX = 413;
        var speed = NoteGeometry.ScrollPixelsPerSecond(120, 1);
        var hitTime = TimeSpan.FromSeconds(10);

        Assert.Equal(1361, hitX + 2 * speed);
        var (from, until) = NoteGeometry.VisibleWindow(hitTime, speed, hitX);
        Assert.InRange(Math.Abs(hitX + (hitTime - from).TotalSeconds * speed - 1380), 0, 0.0001);
        Assert.InRange(Math.Abs(hitX + (hitTime - until).TotalSeconds * speed + 100), 0, 0.0001);
    }

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
        var (from, until) = NoteGeometry.VisibleWindow(TimeSpan.FromSeconds(100), 400, 300);

        Assert.Equal(TimeSpan.FromSeconds(100 - 2.7), from);
        Assert.Equal(TimeSpan.FromSeconds(101), until);
    }

    [Fact]
    public void ANoteThatBarelyMovesIsAlwaysVisible()
    {
        Assert.Equal((TimeSpan.MinValue, TimeSpan.MaxValue), NoteGeometry.VisibleWindow(TimeSpan.Zero, -50, 300));
    }
}
