using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

/// <summary>
/// Every setting config.cfg knows (Golden/settings-input.cfg, each away from its default) parses and saves back unchanged,
/// and a save into a file without them writes exactly Golden/settings-saved.cfg: every key, its format, their order.
/// </summary>
public sealed class ArcadeSettingsGoldenTests
{
    // The source folder's Golden, found above the test binary (CI builds map source paths, so no CallerFilePath).
    private static readonly string Folder = findFolder();

    private static string findFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var folder = Path.Combine(directory.FullName, "tests", "Waddamburo.Game.Tests", "Golden");
            if (Directory.Exists(folder))
                return folder;
        }
        throw new DirectoryNotFoundException("tests/Waddamburo.Game.Tests/Golden not found above " + AppContext.BaseDirectory);
    }

    private static string golden(string name) => Path.Combine(Folder, name);

    [Fact]
    public void EverySettingSavesAndParsesBack()
    {
        var input = File.ReadAllText(golden("settings-input.cfg"));
        var settings = ArcadeSettings.Parse(input);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, input);
            ArcadeSettings.SaveMenuSettings(path, settings, cabinet: true, mode: true);
            Assert.Equal(settings, ArcadeSettings.Parse(File.ReadAllText(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASaveWritesEveryMenuSettingInItsFormatAndOrder()
    {
        var settings = ArcadeSettings.Parse(File.ReadAllText(golden("settings-input.cfg")));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# A file without the settings: the save appends them all.\n");
            ArcadeSettings.SaveMenuSettings(path, settings, cabinet: true, mode: true);
            var saved = File.ReadAllText(path);
            var expected = golden("settings-saved.cfg");
            // ponytail: the golden file is written from the implementation once; delete it to take a deliberate change.
            if (!File.Exists(expected))
            {
                File.WriteAllText(expected, saved);
                Assert.Fail($"{expected} written; run again.");
            }
            Assert.Equal(File.ReadAllText(expected), saved);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
