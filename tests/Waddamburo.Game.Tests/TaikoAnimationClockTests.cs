using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoAnimationClockTests
{
    [Fact]
    public void IntegratesAcrossTempoChangesWithoutRephasing()
    {
        var clock = new TaikoAnimationClock([new(TimeSpan.Zero, 120, 4, 4),
            new(TimeSpan.FromSeconds(1), 240, 4, 4), new(TimeSpan.FromSeconds(2), 60, 3, 4)]);
        Assert.Equal(-60, clock.Position(TimeSpan.FromSeconds(-1)));
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(210, clock.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(210, clock.FramesToAdvance);
        Assert.Equal(0, clock.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, clock.Advance(TimeSpan.FromSeconds(2.9)));
        Assert.Equal(15, clock.Advance(TimeSpan.FromSeconds(3.5)), 6);
    }

    [Fact]
    public void FractionalTicksAccumulateAndFrozenClockDoesNotAnimate()
    {
        var clock = new TaikoAnimationClock([new(TimeSpan.Zero, 60, 4, 4)]);
        clock.Advance(TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(.01));
        Assert.Equal(0, clock.FramesToAdvance);
        clock.Advance(TimeSpan.FromSeconds(.04));
        Assert.Equal(1, clock.FramesToAdvance);
        Assert.Equal(.2f, clock.Interpolation, 5);
        clock.Advance(TimeSpan.FromSeconds(.04));
        Assert.Equal(0, clock.FramesToAdvance);
    }
}
