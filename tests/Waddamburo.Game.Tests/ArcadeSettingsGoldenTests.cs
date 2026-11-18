using System.Runtime.CompilerServices;
using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

/// <summary>
/// Every setting config.cfg knows (Golden/settings-input.cfg, each away from its default) parses and saves back unchanged,
/// and a save into a file without them writes exactly Golden/settings-saved.cfg: every key, its format, their order.
/// </summary>
public sealed class ArcadeSettingsGoldenTests
{
    private static string golden(string name, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Golden", name);

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
