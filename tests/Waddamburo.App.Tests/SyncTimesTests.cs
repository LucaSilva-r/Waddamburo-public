using Waddamburo.App.Sync;

namespace Waddamburo.App.Tests;

public sealed class SyncTimesTests
{
    [Fact]
    public void ReceivedFilesKeepTheSendersTimeUntilChanged()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var song = Path.Combine(folder, "song.tja");
            File.WriteAllText(song, "abc");
            var here = new DateTimeOffset(File.GetLastWriteTimeUtc(song)).ToUnixTimeSeconds();
            File.WriteAllText(Path.Combine(folder, ".waddamburo-times"), $"song.tja\t3\t{here}\t12345\n");

            var local = SyncClient.Local(folder);
            Assert.Equal((3L, 12345L), local["song.tja"]);
            Assert.False(local.ContainsKey(".waddamburo-times"));

            File.WriteAllText(song, "abcd"); // changed here: its own time again
            Assert.NotEqual(12345L, SyncClient.Local(folder)["song.tja"].Time);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
