using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class ArcadeTests
{
    [Fact]
    public void DefaultFileParsesToTheDefaults() =>
        Assert.Equal(new ArcadeSettings(), ArcadeSettings.Parse(ArcadeSettings.DefaultFileText));

    [Fact]
    public void ParsesEverySettingAndIgnoresComments()
    {
        var settings = ArcadeSettings.Parse("""
            free_play = false   # coins
            credits_per_coin = 2
            credits_1p = 2
            credits_2p = 4
            songs_per_session = 3
            """);
        Assert.Equal(new ArcadeSettings
        {
            FreePlay = false, CreditsPerCoin = 2, CreditsOnePlayer = 2, CreditsTwoPlayers = 4, SongsPerSession = 3,
        }, settings);
    }

    [Theory]
    [InlineData("unknown = 1")]
    [InlineData("credits_1p = 0")]
    [InlineData("free_play = maybe")]
    [InlineData("credits_1p = 3\ncredits_2p = 2")]
    public void RejectsInvalidSettings(string text) =>
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse(text));

    [Fact]
    public void CoinsAreCreditedAtOnceAndSpentOnJoin()
    {
        // The traced cabinet: 2 credits per player, 4 for two.
        var bank = new CoinBank(new ArcadeSettings { FreePlay = false, CreditsOnePlayer = 2, CreditsTwoPlayers = 4 });
        bank.InsertCoin();
        Assert.Equal((1, 1), (bank.Credits, bank.Missing(0)));
        Assert.False(bank.TryJoin(0));
        bank.InsertCoin();
        Assert.True(bank.TryJoin(0));
        Assert.Equal((0, 2), (bank.Credits, bank.Missing(1)));
    }
}
