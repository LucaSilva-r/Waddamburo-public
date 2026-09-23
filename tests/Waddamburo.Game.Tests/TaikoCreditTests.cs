using Waddamburo.Game.Lumen;

namespace Waddamburo.Game.Tests;

public sealed class TaikoCreditTests
{
    [Theory]
    [InlineData(1, false, 2, 2)] // failed first song: revival
    [InlineData(1, true, 2, 1)]  // one more song
    [InlineData(2, false, 2, 0)] // last song of the credit, cleared or not (traced)
    [InlineData(2, true, 2, 0)]
    [InlineData(2, true, 3, 1)]  // a three-song session continues
    [InlineData(1, false, 1, 2)] // revival even in a one-song session
    public void PicksTheResultsEndMessage(int stage, bool cleared, int songs, int expected) =>
        Assert.Equal(expected, TaikoCredit.EndMessage(stage, cleared, songs));
}
