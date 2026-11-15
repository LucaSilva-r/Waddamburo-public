using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class RevivalChallengeTests
{
    [Fact]
    public void HitsCountAfterTheLeadInAndTheNormaInTimeSucceeds()
    {
        var roll = new RevivalChallenge(norma: 5, seconds: 2);
        for (var tick = 0; tick < 90; tick++)
            roll.Tick(hits: 3); // the lead-in: not counted
        Assert.Equal(0, roll.Hits);
        Assert.Null(roll.TimeLeft);
        roll.Tick(hits: 2);
        Assert.Equal(2, roll.Hits);
        Assert.Equal(2 - 1 / 60.0, roll.TimeLeft!.Value, 6);
        roll.Tick(hits: 4);
        Assert.Equal(5, roll.Hits);
        Assert.True(roll.Succeeded);
        Assert.Null(roll.TimeLeft);
    }

    [Fact]
    public void TimeRunningOutFirstFails()
    {
        var roll = new RevivalChallenge(norma: 5, seconds: 1);
        for (var tick = 0; tick < 90 + 60; tick++)
            roll.Tick(hits: tick % 30 == 0 ? 1 : 0);
        Assert.False(roll.Succeeded);
        Assert.True(roll.Hits < 5);
    }
}
