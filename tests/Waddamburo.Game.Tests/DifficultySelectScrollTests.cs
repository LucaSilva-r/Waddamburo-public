using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class DifficultySelectScrollTests
{
    [Fact]
    public void RapidHitsWaitForLogicalSelectionChanges()
    {
        var selected = 0;
        var queue = new DifficultySelectScroll(side => side == 0 ? selected : null);
        for (var tick = 0; tick < 36; tick++)
        {
            var input = queue.Before(tick is 0 or 2 or 4 ? new(['S']) : LumenInputSnapshot.Empty, side => side == 0);
            // The native movie accepts input only after its current move finishes.
            if (tick % 6 == 0 && input.IsDown('S')) selected++;
            queue.After();
        }
        Assert.Equal(3, selected);
    }

    [Fact]
    public void SecretChartAtTheSameScreenPositionStillConsumesOneHit()
    {
        double selected = 3;
        var queue = new DifficultySelectScroll(side => selected);
        Assert.True(queue.Before(new(['S']), side => side == 0).IsDown('S'));
        selected = 4;
        queue.After();
        Assert.False(queue.Before(LumenInputSnapshot.Empty, side => side == 0).IsDown('S'));
    }

    [Fact]
    public void OuterRightEdgeDoesNotReplaySecretUnlockHits()
    {
        var queue = new DifficultySelectScroll(side => 3);
        Assert.True(queue.Before(new(['S']), side => side == 0).IsDown('S'));
        queue.After();
        Assert.False(queue.Before(LumenInputSnapshot.Empty, side => side == 0).IsDown('S'));
    }

    [Fact]
    public void ConfirmationAndUtilityMenusCancelPendingMovement()
    {
        var queue = new DifficultySelectScroll(side => 0);
        queue.Before(new(['S']), side => side == 0);
        var confirm = queue.Before(new(['Z']), side => side == 0);
        Assert.True(confirm.IsDown('Z'));
        Assert.False(confirm.IsDown('S'));
        queue.Before(new(['S']), side => side == 0);
        Assert.True(queue.Before(new(['A']), _ => false).IsDown('A'));
        Assert.False(queue.Before(LumenInputSnapshot.Empty, side => side == 0).IsDown('S'));
    }

    [Fact]
    public void ReversalDropsStaleDirectionAndAnUnreachableEdgeExpires()
    {
        var queue = new DifficultySelectScroll(side => 0);
        queue.Before(new(['S']), side => side == 0);
        var reversed = queue.Before(new(['A']), side => side == 0);
        Assert.True(reversed.IsDown('A'));
        Assert.False(reversed.IsDown('S'));
        for (var tick = 0; tick < 31; tick++) queue.Before(LumenInputSnapshot.Empty, side => side == 0);
        Assert.Empty(queue.Before(LumenInputSnapshot.Empty, side => side == 0).PressedKeyCodes);
    }
}
