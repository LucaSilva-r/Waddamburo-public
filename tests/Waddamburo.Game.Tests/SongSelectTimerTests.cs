using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class SongSelectTimerTests
{
    [Fact]
    public void CountdownExpiresWithoutRenderTicksAndClampsAtZero()
    {
        var clock = new ManualClock();
        var timer = new SongSelectTimer(clock);
        Assert.False(timer.IsTimeUp);
        timer.Start(5);
        clock.Seconds = 4.5;
        Assert.Equal(TimeSpan.FromSeconds(.5), timer.Remaining);
        Assert.False(timer.IsTimeUp);
        clock.Seconds = 8;
        Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.True(timer.IsTimeUp);
    }

    [Fact]
    public void StopFreezesRemainingTimeAndRestartReplacesDuration()
    {
        var clock = new ManualClock();
        var timer = new SongSelectTimer(clock);
        timer.Start(10);
        clock.Seconds = 3;
        timer.Stop();
        clock.Seconds = 100;
        Assert.Equal(TimeSpan.FromSeconds(7), timer.Remaining);
        Assert.False(timer.IsTimeUp);
        timer.Start(2);
        clock.Seconds = 102;
        Assert.True(timer.IsTimeUp);
        timer.Stop();
        Assert.False(timer.IsTimeUp);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDurationDoesNotReplaceRunningTimer(double seconds)
    {
        var timer = new SongSelectTimer(new ManualClock());
        timer.Start(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Start(seconds));
        Assert.Equal(TimeSpan.FromSeconds(3), timer.Remaining);
    }

    private sealed class ManualClock : TimeProvider
    {
        public double Seconds { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)(Seconds * TimestampFrequency);
    }
}
