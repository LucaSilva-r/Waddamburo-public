using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class ArcadeTests
{
    [Fact]
    public void LanguagesDefaultToTheSystems()
    {
        var japanese = ArcadeSettings.SystemLanguage == "ja";
        Assert.Equal((ArcadeSettings.SystemLanguage, !japanese), (ArcadeSettings.Parse("").Language, ArcadeSettings.Parse("").EnglishTitles));
        Assert.False(ArcadeSettings.Parse("title_language = japanese").EnglishTitles);
        Assert.Equal("ja", ArcadeSettings.Parse("language = JA").Language);
    }

    [Fact]
    public void UpgradeReplacesTheOldTitleLanguageWithTheSystems()
    {
        var upgraded = ArcadeSettings.Upgrade("""
            config_version = 13
            # Song titles in english (translated, when known) or japanese (the original).
            title_language = english
            music_volume = 40
            """);
        var settings = ArcadeSettings.Parse(upgraded);
        Assert.Equal((ArcadeSettings.SystemLanguage, ArcadeSettings.SystemLanguage != "ja", 40),
            (settings.Language, settings.EnglishTitles, settings.MusicVolume));
        Assert.Single(upgraded.Split('\n'), static line => line.StartsWith("title_language", StringComparison.Ordinal));
        Assert.Single(upgraded.Split('\n'), static line => line.StartsWith("# Song titles", StringComparison.Ordinal));
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
        Assert.Contains("p1_left_ka = d,", upgraded, StringComparison.Ordinal);
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
            ArcadeSettings.SaveMenuSettings(path, new ArcadeSettings { MusicVolume = 40, AudioOffsetMs = -25, StereoPanning = false });
            var text = File.ReadAllText(path);
            var saved = ArcadeSettings.Parse(text);
            Assert.Equal((40, -25, 50, false), (saved.MusicVolume, saved.AudioOffsetMs, saved.MasterVolume, saved.FreePlay));
            Assert.False(saved.StereoPanning);
            Assert.True(saved.MuteInBackground);
            Assert.StartsWith("# mine\nfree_play = false\nmusic_volume = 40\n", text, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("master_volume = 101"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ServerDefaultsToTaikOnlineAndEmptyIsOffline()
    {
        var defaults = ArcadeSettings.Parse(ArcadeSettings.DefaultFileText);
        Assert.Equal(new Uri("https://taikonline.com/"), defaults.Server);
        Assert.False(defaults.ServerInsecure);
        Assert.Null(ArcadeSettings.Parse("server =").Server);
    }

    [Fact]
    public void RendererIsAutoOrABackend()
    {
        Assert.Null(ArcadeSettings.Parse(ArcadeSettings.DefaultFileText).Renderer);
        Assert.Equal("gles", ArcadeSettings.Parse("renderer = GLES").Renderer);
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("renderer = d3d9"));
    }

    [Fact]
    public void TimingIndicatorDefaultsToGoodAndBadAndSavesBack()
    {
        Assert.Equal(TimingIndicator.GoodBad, ArcadeSettings.Parse(ArcadeSettings.DefaultFileText).TimingIndicator);
        Assert.Equal(TimingIndicator.Always, ArcadeSettings.Parse("timing_indicator = Always").TimingIndicator);
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("timing_indicator = sometimes"));
        var path = Path.GetTempFileName();
        try
        {
            var settings = ArcadeSettings.Parse(ArcadeSettings.DefaultFileText) with { TimingIndicator = TimingIndicator.Off };
            ArcadeSettings.SaveMenuSettings(path, settings);
            Assert.Equal(TimingIndicator.Off, ArcadeSettings.Parse(File.ReadAllText(path)).TimingIndicator);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DisplaySettingsParseAndSaveBack()
    {
        var settings = ArcadeSettings.Parse("fullscreen_mode = exclusive\nfullscreen_resolution = 1920x1080\nrefresh_rate = 144\nvsync = false\nletterbox_size = 80");
        Assert.Equal((true, 1920, 1080, 144, false, 80),
            (settings.ExclusiveFullscreen, settings.FullscreenWidth, settings.FullscreenHeight, settings.RefreshRate, settings.Vsync, settings.LetterboxSize));
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("fullscreen_resolution = 1920"));
        var path = Path.GetTempFileName();
        try
        {
            // Two keys on one pad, as the settings menu binds them.
            settings = settings with { Controls = settings.Controls.With(1, "f, g, pad1:south") };
            ArcadeSettings.SaveMenuSettings(path, settings);
            Assert.Equal(settings, ArcadeSettings.Parse(File.ReadAllText(path)) with { Version = settings.Version });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CacheFolderDefaultsToThisPcsUserCache()
    {
        var home = Path.Combine(Path.GetTempPath(), "usb", "waddamburo");
        string root(string text) => ArcadeSettings.CacheRoot(home, ArcadeSettings.Parse(text).CacheFolder);
        Assert.Equal(ArcadeSettings.UserCacheFolder, root(ArcadeSettings.DefaultFileText));
        Assert.Equal(ArcadeSettings.UserCacheFolder, root("cache_folder =\n"));
        Assert.Equal(ArcadeSettings.UserCacheFolder, root("cache_folder = User\n"));
        Assert.Equal(Path.Combine(home, "cache"), root("cache_folder = game\n"));
        var custom = Path.Combine(Path.GetTempPath(), "fast-cache");
        Assert.Equal(custom, root($"cache_folder = {custom}\n"));
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

    [Fact]
    public void UpgradingKeepsASettingTheFileHasAlready()
    {
        // The menu saved menu_volume into a version 17 file before version 18 added it.
        var upgraded = ArcadeSettings.Upgrade("config_version = 17\nfree_play = true\nmenu_volume = 70\n");

        Assert.Equal(70, ArcadeSettings.Parse(upgraded).MenuVolume);
        Assert.Single(upgraded.Split('\n'), static line => line.StartsWith("menu_volume", StringComparison.Ordinal));
        Assert.Contains("button_scores = l, pad1:north", upgraded, StringComparison.Ordinal);
    }
}
