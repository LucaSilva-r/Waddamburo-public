using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class SongSelectScrollSettingsTests
{
    [Fact]
    public void FreshClassicInstallEnablesFastScrolling()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = ArcadeSettings.LoadOrCreate(Path.Combine(directory, ArcadeSettings.FileName));
            Assert.Equal(SongSelectScrollMode.Classic, settings.SongSelectScroll);
            Assert.True(settings.FastSongScroll);
            Assert.True(ArcadeSettings.Parse("").FastSongScroll);
            Assert.True(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 1\n")).FastSongScroll);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradingAndSavingPreservesTheExplicitFastScrollingChoice(bool enabled)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, $"config_version = 1\nfast_song_scroll = {enabled.ToString().ToLowerInvariant()}\n");
            var settings = ArcadeSettings.LoadOrCreate(file);
            Assert.Equal(enabled, settings.FastSongScroll);
            ArcadeSettings.SaveMenuSettings(file, settings with { SongSelectScroll = SongSelectScrollMode.Vertical });
            var saved = ArcadeSettings.LoadOrCreate(file);
            Assert.Equal(enabled, saved.FastSongScroll);
            Assert.Equal(SongSelectScrollMode.Vertical, saved.SongSelectScroll);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void WaiwaiIsOptInAndSurvivesAConfigUpgrade()
    {
        Assert.False(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 22\n")).WaiwaiEnabled);
        Assert.True(ArcadeSettings.Parse(ArcadeSettings.Upgrade("config_version = 22\nwaiwai_enabled = true\n")).WaiwaiEnabled);
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse("waiwai_enabled = sometimes"));
    }

    [Fact]
    public void UpgradeKeepsTheExistingChoicesAndAddsDefaultsForOldFiles()
    {
        var text = ArcadeSettings.Upgrade("config_version = 21\nmusic_volume = 37\n");
        var settings = ArcadeSettings.Parse(text);
        Assert.Equal(SongSelectScrollMode.Classic, settings.SongSelectScroll);
        Assert.Equal(VerticalScrollPath.Linear, settings.VerticalScrollPath);
        Assert.Equal(37, settings.MusicVolume);
        Assert.Same(text, ArcadeSettings.Upgrade(text));
        var chosen = ArcadeSettings.Parse(ArcadeSettings.Upgrade(
            "config_version = 21\nsong_select_scroll = vertical\nvertical_scroll_path = curved\n"));
        Assert.Equal(SongSelectScrollMode.Vertical, chosen.SongSelectScroll);
        Assert.Equal(VerticalScrollPath.Curved, chosen.VerticalScrollPath);
    }

    [Theory]
    [InlineData(SongSelectScrollMode.Classic, VerticalScrollPath.Linear)]
    [InlineData(SongSelectScrollMode.Classic, VerticalScrollPath.Curved)]
    [InlineData(SongSelectScrollMode.Vertical, VerticalScrollPath.Linear)]
    [InlineData(SongSelectScrollMode.Vertical, VerticalScrollPath.Curved)]
    public void SavingAndReloadingKeepsTheLayoutAndTheRememberedVerticalPath(
        SongSelectScrollMode mode, VerticalScrollPath path)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "# user settings\nmusic_volume = 37\n");
            var settings = ArcadeSettings.Parse(File.ReadAllText(file)) with
                { SongSelectScroll = mode, VerticalScrollPath = path, WaiwaiEnabled = mode == SongSelectScrollMode.Vertical };
            ArcadeSettings.SaveMenuSettings(file, settings);
            var saved = ArcadeSettings.LoadOrCreate(file);
            Assert.Equal(mode, saved.SongSelectScroll);
            Assert.Equal(path, saved.VerticalScrollPath);
            Assert.Equal(settings.WaiwaiEnabled, saved.WaiwaiEnabled);
            Assert.Equal(37, saved.MusicVolume);
            Assert.StartsWith("# user settings", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("song_select_scroll = diagonal")]
    [InlineData("vertical_scroll_path = circle")]
    public void InvalidLayoutValuesAreReported(string text) =>
        Assert.Throws<InvalidDataException>(() => ArcadeSettings.Parse(text));
}
