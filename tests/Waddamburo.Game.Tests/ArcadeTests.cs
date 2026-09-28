using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class ArcadeTests
{
    [Fact]
    public void TitleLanguageDefaultsToEnglish()
    {
        Assert.True(ArcadeSettings.Parse("").EnglishTitles);
        Assert.False(ArcadeSettings.Parse("title_language = japanese").EnglishTitles);
    }

    [Fact]
    public void DefaultFileParsesToTheDefaults() =>
        Assert.Equal(new ArcadeSettings { Version = ArcadeSettings.CurrentVersion, Home = true },
            ArcadeSettings.Parse(ArcadeSettings.DefaultFileText));

    [Fact]
    public void UpgradeChoosesTheModeFromTheCabinetsOwnSettings()
    {
        // A free-play PC becomes home; a cabinet (coins, or a cabinet token) stays arcade.
        Assert.True(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 1\nfree_play = true\n")).Home);
        Assert.False(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 1\ncabinet_token = abc\n")).Home);
        Assert.False(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 1\nfree_play = false\n")).Home);
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("mode = party"));
    }

    [Fact]
    public void UpgradeKeepsEditsAndAddsNewerSettings()
    {
        // A file written before config_version existed, with the user's own edits.
        var upgraded = ArcadeSettings.Upgrade("""
            # my cabinet
            free_play = false
            songs_per_session = 4
            """);
        Assert.Equal(new ArcadeSettings
        {
            Version = ArcadeSettings.CurrentVersion, FreePlay = false, SongsPerSession = 4,
        }, ArcadeSettings.Parse(upgraded));
        Assert.Contains("# my cabinet", upgraded, StringComparison.Ordinal);
        Assert.Contains("auto_update = true", upgraded, StringComparison.Ordinal);
        Assert.Same(upgraded, ArcadeSettings.Upgrade(upgraded));
        Assert.Equal(ArcadeSettings.DefaultFileText, ArcadeSettings.Upgrade(ArcadeSettings.DefaultFileText));
    }

    [Fact]
    public void MenuSettingsAreSavedInPlaceKeepingTheRestOfTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# mine\nfree_play = false\nmusic_volume = 100 # loud\n");
            ArcadeSettings.SaveMenuSettings(path, new ArcadeSettings { MusicVolume = 40, AudioOffsetMs = -25 });
            var text = File.ReadAllText(path);
            var saved = ArcadeSettings.Parse(text);
            Assert.Equal((40, -25, 50, false), (saved.MusicVolume, saved.AudioOffsetMs, saved.MasterVolume, saved.FreePlay));
            Assert.StartsWith("# mine\nfree_play = false\nmusic_volume = 40\n", text, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("master_volume = 101"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileFromANewerBuildMayHoldUnknownSettings() =>
        Assert.True(ArcadeSettings.Parse($"config_version = {ArcadeSettings.CurrentVersion + 1}\nfuture = 1").FreePlay);

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
