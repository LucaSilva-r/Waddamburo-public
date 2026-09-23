using Waddamburo.Game.Lumen;

namespace Waddamburo.Game.Tests;

public sealed class TaikoCreditTests
{
    [Theory]
    [InlineData(1, false, 2)] // failed first song: revival
    [InlineData(1, true, 1)]  // one more song
    [InlineData(2, false, 0)] // last song of the credit, cleared or not (traced)
    [InlineData(2, true, 0)]
    public void PicksTheResultsEndMessage(int stage, bool cleared, int expected) =>
        Assert.Equal(expected, TaikoCredit.EndMessage(stage, cleared));
}
