namespace Waddamburo.Editor.Tests;

public sealed class EditorReadoutTests
{
    [Fact]
    public void PlaybackRefreshIsLimitedWhilePausedEditsAndLargeSeeksRefreshImmediately()
    {
        var readout = new EditorReadout();
        readout.Update(1000, TimeSpan.FromSeconds(1), 2, false);
        readout.Update(1050, TimeSpan.FromSeconds(1.05), 2.1, false);
        Assert.Equal(2, readout.Beat);
        readout.Update(1100, TimeSpan.FromSeconds(1.1), 2.2, false); Assert.Equal(2.2, readout.Beat);
        readout.Update(1110, TimeSpan.FromSeconds(1.123), 2.246, true); Assert.Equal(2.246, readout.Beat);
        readout.Update(1120, TimeSpan.FromSeconds(10), 20, false); Assert.Equal(20, readout.Beat);
    }
}
