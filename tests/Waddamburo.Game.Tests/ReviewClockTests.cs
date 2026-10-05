using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class ReviewClockTests
{
    private static TimeSpan s(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void RunsAtItsSpeedPausesAndStopsAtTheEnd()
    {
        var clock = new ReviewClock(s(-3), s(10));
        clock.Update(s(2));
        Assert.Equal(s(-1), clock.Position);
        clock.Slower(); // 0.75
        clock.Slower(); // 0.5
        clock.Update(s(2));
        Assert.Equal(s(0), clock.Position);
        clock.TogglePause();
        clock.Update(s(5));
        Assert.Equal(s(0), clock.Position);
        clock.TogglePause();
        for (var i = 0; i < 10; i++)
            clock.Faster();
        Assert.Equal(2, clock.Speed);
        clock.Update(s(100));
        Assert.Equal((s(10), true), (clock.Position, clock.Paused));
        clock.TogglePause(); // from the end: starts over
        Assert.Equal((s(-3), false), (clock.Position, clock.Paused));
    }

    [Fact]
    public void SeeksStayInsideThePlayAndReportTheJump()
    {
        var clock = new ReviewClock(s(-3), s(10));
        var jumps = new List<(TimeSpan From, TimeSpan To)>();
        clock.Sought += (from, to) => jumps.Add((from, to));
        clock.Seek(s(4));
        clock.SeekBy(s(-20));
        clock.SeekBy(s(-1)); // already at the start: no jump
        Assert.Equal([(s(-3), s(4)), (s(4), s(-3))], jumps);
    }

    [Fact]
    public void HoldingASeekKeyScrubsFasterAndLettingGoStopsAtOnce()
    {
        var clock = new ReviewClock(s(-3), s(200));
        clock.TogglePause(); // paused before scrubbing
        var step = TimeSpan.FromMilliseconds(100);
        var paces = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            clock.Update(step, scrub: +1);
            paces.Add(clock.Rate);
        }
        Assert.True(paces.Zip(paces.Skip(1)).All(static pair => pair.Second >= pair.First)); // it only speeds up
        Assert.Equal(2, paces[0], 1);
        Assert.Equal(40, paces[^1], 3);
        var held = clock.Position;
        clock.Update(step); // let go: still paused, standing still
        Assert.Equal((held, 0d, true, false), (clock.Position, clock.Rate, clock.Paused, clock.Scrubbing));
        clock.Update(step, scrub: -1); // a fresh hold starts slow again
        Assert.Equal(-2, clock.Rate, 1);
    }
}
