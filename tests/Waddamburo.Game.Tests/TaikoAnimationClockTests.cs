using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoAnimationClockTests
{
    [Fact]
    public void StepsAsTracedBpmOver89FramesPerTick()
    {
        // Traced step 44.5 at 120 BPM against 60 per tick: 60 / 44.5 frames per tick.
        var clock = new TaikoAnimationClock([new(TimeSpan.Zero, 120, 4, 4)]);
        Assert.Equal(60 / 44.5, clock.Position(TimeSpan.FromSeconds(1 / 60d)), 4); // TimeSpan rounds 1/60 s
    }

    [Fact]
    public void IntegratesAcrossTempoChangesWithoutRephasing()
    {
        var clock = new TaikoAnimationClock([new(TimeSpan.Zero, 120, 4, 4),
            new(TimeSpan.FromSeconds(1), 240, 4, 4), new(TimeSpan.FromSeconds(2), 60, 3, 4)]);
        const double perBpmSecond = 60d / 89;
        Assert.Equal(-120 * perBpmSecond, clock.Position(TimeSpan.FromSeconds(-1)), 6);
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(420 * perBpmSecond, clock.Advance(TimeSpan.FromSeconds(3)), 6);
        Assert.Equal(283, clock.FramesToAdvance);
        Assert.Equal(0, clock.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, clock.Advance(TimeSpan.FromSeconds(2.9)));
        Assert.Equal(30 * perBpmSecond, clock.Advance(TimeSpan.FromSeconds(3.5)), 6);
    }

    [Fact]
    public void FractionalTicksAccumulateAndFrozenClockDoesNotAnimate()
    {
        // 89 BPM: one frame per 60 Hz tick, 60 a second.
        var clock = new TaikoAnimationClock([new(TimeSpan.Zero, 89, 4, 4)]);
        clock.Advance(TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(.01));
        Assert.Equal(0, clock.FramesToAdvance);
        clock.Advance(TimeSpan.FromSeconds(.04));
        Assert.Equal(2, clock.FramesToAdvance);
        Assert.Equal(.4f, clock.Interpolation, 4);
        clock.Advance(TimeSpan.FromSeconds(.04));
        Assert.Equal(0, clock.FramesToAdvance);
    }
}
