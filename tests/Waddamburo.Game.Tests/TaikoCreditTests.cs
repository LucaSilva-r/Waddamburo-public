using Waddamburo.Game.Lumen;

namespace Waddamburo.Game.Tests;

public sealed class TaikoCreditTests
{
    [Theory]
    [InlineData(1, "F", 2, TaikoCreditNext.Revival, 2)]  // one player failed the first song
    [InlineData(1, "C", 2, TaikoCreditNext.NextSong, 0)] // traced 0 after a cleared song
    [InlineData(2, "F", 2, TaikoCreditNext.End, 0)]      // last song, cleared or not (traced)
    [InlineData(2, "C", 3, TaikoCreditNext.NextSong, 0)] // a three-song session continues
    [InlineData(1, "F", 1, TaikoCreditNext.Revival, 2)]  // revival even in a one-song session
    [InlineData(1, "FC", 2, TaikoCreditNext.NextSong, 1)] // two players, one failed (traced session9-2p)
    [InlineData(1, "CC", 2, TaikoCreditNext.NextSong, 0)]
    [InlineData(1, "FF", 2, TaikoCreditNext.End, 0)]      // two players: no revival
    [InlineData(2, "CC", 2, TaikoCreditNext.End, 0)]      // traced 0 at the credit's end
    [InlineData(2, "FC", 3, TaikoCreditNext.NextSong, 0)] // "another chance" is for the first song only
    public void PicksWhatFollowsTheResults(int stage, string players, int songs, TaikoCreditNext next, int message)
    {
        bool[] cleared = [.. players.Select(static player => player == 'C')];
        Assert.Equal(next, TaikoCredit.Next(stage, cleared, songs));
        Assert.Equal(message, TaikoCredit.EndMessage(stage, cleared, songs));
    }
}
