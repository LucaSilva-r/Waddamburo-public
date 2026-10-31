using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class DanCatalogueTests
{
    private static DanCourseSong song(string key, params string[] charts) => new(key, 3, null, 100, [.. charts]);

    private static DanCourse course(params DanCourseSong[] songs) => new(1, "Test", "hash", [], [.. songs]);

    private static DanLibraryChart owned(string key, string sha256, int course = 3) =>
        new(key, course, new ChartKey(new SongKey(SongSourceKind.Stock, key), "chart"), sha256);

    [Fact]
    public void CourseIsPlayableOnlyWhenEverySongHasAChartThatCounts()
    {
        var test = course(song("a", "ha"), song("b", "hb"));

        Assert.True(DanCourseStatus.Of(test, [owned("a", "ha"), owned("b", "hb")]).Playable);
        Assert.False(DanCourseStatus.Of(test, [owned("a", "ha")]).Playable);
    }

    [Fact]
    public void SameSongWithAnotherChartDoesNotCountButIsToldApartFromMissing()
    {
        var status = DanCourseStatus.Of(course(song("a", "ha"), song("b", "hb"), song("c", "hc")),
            [owned("a", "other"), owned("b", "other2", course: 2)]);

        Assert.False(status.Playable);
        Assert.Equal(DanSongState.DifferentChart, status.Songs[0].State);
        Assert.Equal(DanSongState.Missing, status.Songs[1].State); // another course of the song
        Assert.Equal(DanSongState.Missing, status.Songs[2].State);
    }

    [Fact]
    public void ChartCountsByItsHashWhateverItsSongId()
    {
        var approved = owned("title:Custom", "approved");

        var status = DanCourseStatus.Of(course(song("a", "ha", "approved")), [approved]);

        Assert.True(status.Playable);
        Assert.Equal(approved.Chart, status.Songs[0].Chart);
    }

    [Fact]
    public void ReadsTheServersJsonAndKeepsItForOfflineStarts()
    {
        const string served = """
            {"series":[{"id":"green","name":"Green","courses":[{"level":1,"name":"初級","hash":"h",
            "requirements":[{"type":"soul_gauge","red":[90],"gold":[95]},{"type":"rolls","red":[10,0,5],"gold":[12,0,6]}],
            "songs":[{"song_key":"eva","course":0,"title":null,"notes":103,"charts":["sha"]}]}]}]}
            """;
        var catalogue = JsonSerializer.Deserialize<DanCatalogue>(served, ScoreClient.Json)!;
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(folder, "dan.json");
        try
        {
            catalogue.Save(path);
            var loaded = DanCatalogue.Load(path)!;

            var first = Assert.Single(Assert.Single(loaded.Series).Courses);
            Assert.Equal([10, 0, 5], first.Requirements[1].Red);
            Assert.Equal("eva", Assert.Single(first.Songs).SongKey);
            Assert.Equal("sha", Assert.Single(first.Songs[0].Charts));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MissingOrBrokenSaveIsNoCatalogue()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(DanCatalogue.Load(path));
            File.Delete(path);
            Assert.Null(DanCatalogue.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
