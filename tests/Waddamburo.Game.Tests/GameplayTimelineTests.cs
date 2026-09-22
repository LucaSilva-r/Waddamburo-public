using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class GameplayTimelineTests
{
    [Theory]
    [InlineData(-2, 3, 1)]
    [InlineData(0, 3, 3)]
    [InlineData(2, 3, 5)]
    [InlineData(-5, 5, 0)]
    public void ChartZeroAndAudioZeroHaveTheAuthoredSeparation(int offset, int leadIn, int audioStart)
    {
        var timeline = new GameplayTimeline(TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(leadIn), timeline.LeadIn);
        Assert.Equal(TimeSpan.FromSeconds(audioStart), timeline.AudioStart);
        Assert.Equal(TimeSpan.Zero, timeline.ChartTime(timeline.LeadIn));
        Assert.Equal(TimeSpan.FromSeconds(offset), timeline.ChartTime(timeline.AudioStart));
    }

    [Fact]
    public void TransportAdvanceDoesNotDependOnNumberOfAnimationUpdates()
    {
        var timeline = new GameplayTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(3));
        var before = timeline.ChartTime(TimeSpan.FromSeconds(4));
        var after = timeline.ChartTime(TimeSpan.FromSeconds(4.25));
        Assert.Equal(TimeSpan.FromMilliseconds(250), after - before);
    }
}
