using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoSharedKusudamaTests
{
    [Fact]
    public void BothPlayersCountDownOneBallAndAnExpiredBallDoesNotCarryOver()
    {
        var ball = new TaikoSharedKusudama(null!, 2);
        ball.Enter(99);
        ball.Enter(99);
        for (var hit = 0; hit < 48; hit++) ball.Hit();
        Assert.Equal(150, ball.Remaining);
        ball.Leave(); // time ran out for both
        ball.Leave();

        ball.Enter(3);
        ball.Enter(3);
        Assert.Equal(6, ball.Remaining);
        for (var hit = 0; hit < 5; hit++) ball.Hit();
        Assert.False(ball.Popped);
        Assert.Equal(0, ball.Hit()); // the sixth hit breaks it for both
        Assert.True(ball.Popped);
    }
}
