using System.Text.RegularExpressions;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Tests;

public sealed class AlsaDevicesTests
{
    // Every listed device is a plughw name ALSA can open, with a card: output (n) label; no sound cards lists none.
    [Fact]
    public void PlaybackDevicesArePlughwNames()
    {
        if (!OperatingSystem.IsLinux())
            return;
        foreach (var device in AlsaOutput.PlaybackDevices())
        {
            Assert.Matches(new Regex(@"^plughw:CARD=[^,\s]+,DEV=\d+$"), device);
            Assert.Matches(new Regex(@"^.+: .* \(\d+\)$"), AlsaOutput.Label(device));
        }
    }
}
