namespace Waddamburo.Editor.Tests;

public sealed class ChartFileWatchTests
{
    [Fact]
    public void DebouncesRetriesIncompleteSavesAndKeepsTheLastGoodState()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-watch-");
        try
        {
            var path = Path.Combine(folder.FullName, "Song.tja"); File.WriteAllText(path, "synthetic");
            long now = 0; var state = 1;
            using var watch = new ChartFileWatch(path, () => now);
            watch.Schedule(); now = 200;
            Assert.False(watch.Poll(() => state = 2)); Assert.Equal(1, state);
            now = 250;
            Assert.False(watch.Poll(() => throw new InvalidDataException("Incomplete save")));
            Assert.Equal(1, state); Assert.NotNull(watch.Error);
            now = 500; Assert.False(watch.Poll(() => state = 2));
            now = 1000; Assert.True(watch.Poll(() => state = 2));
            Assert.Equal(2, state); Assert.Null(watch.Error);
            Assert.False(watch.Poll(() => state = 3)); Assert.Equal(2, state);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task AtomicReplacementSavesAreObservedWithoutWatchingTheTemporaryFile()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-watch-");
        try
        {
            var path = Path.Combine(folder.FullName, "Song.tja"); File.WriteAllText(path, "before");
            using var watch = new ChartFileWatch(path);
            var temporary = Path.Combine(folder.FullName, "Song.tmp"); File.WriteAllText(temporary, "after");
            File.Move(temporary, path, true);
            var observed = "before";
            for (var i = 0; i < 100 && observed != "after"; i++)
            { watch.Poll(() => observed = File.ReadAllText(path)); await Task.Delay(20); }
            Assert.Equal("after", observed);
        }
        finally { folder.Delete(true); }
    }
}
