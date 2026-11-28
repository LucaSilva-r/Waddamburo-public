using Waddamburo.Game.Scenes;

namespace Waddamburo.Game.Tests;

public sealed class SkinScenesTests
{
    private const string Song = """
        { "format": 1, "scenes": { "song-select": { "layers": [
          { "movie": "indicator/player_name", "role": "indicator-part", "x": 948, "y": 638, "side": 1 },
          { "movie": "song_select/song_select", "role": "song-select" },
          { "movie": "indicator/player_name", "role": "indicator-part", "x": 60, "y": 638, "side": 0 } ] } } }
        """;

    [Fact]
    public void ALayerBecomesTheEnginesLayerAndPerDrumScreensKeepTheirSides()
    {
        var scenes = SkinScenes.Parse(Song);
        var right = scenes.Scene("song-select", [1])!;
        Assert.Equal(["song-select", "indicator-part"], right.Layers.Select(static layer => layer.HostId));
        Assert.Equal(948, right.Layers[1].Transform.X);
        // Both drums: shared layers first, then each side in the order asked.
        var both = scenes.Scene("song-select", [1, 0])!;
        Assert.Equal([0f, 948, 60], both.Layers.Select(static layer => layer.Transform.X));
        Assert.Equal(("song_select/packeddata.ddp", "song_select/song_select.lm"), (both.Layers[0].ArchiveId, both.Layers[0].MovieId));
        Assert.Equal(3, scenes.Scene("song-select")!.Layers.Length);
        Assert.Null(scenes.Scene("entry"));
    }

    [Theory]
    [InlineData("""{ "format": 1, "scenes": { "entry": { "layers": [ { "movie": "entry/entry", "role": "decor" } ] } } }""", "player-entry")]
    [InlineData("""{ "format": 1, "scenes": { "lobby": { "layers": [] } } }""", "no screen")]
    [InlineData("""{ "format": 1, "scenes": { "boot": { "layers": [ { "movie": "../boot", "role": "boot" } ] } } }""", "package folder")]
    [InlineData("""{ "format": 1, "scenes": { "boot": { "layers": [ { "movie": "a/boot", "role": "boot", "side": 2 } ] } } }""", "side")]
    [InlineData("""{ "format": 2, "scenes": {} }""", "format")]
    [InlineData("""{ "format": 1, "scenes": { "boot": { "layers": [ { "movie": "a/boot", "role": "boot" } ], "opensFrom": ["lobby"] } } }""", "opensFrom")]
    public void MistakesAreRefusedByName(string json, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => SkinScenes.Parse(json)).Message);

    [Fact]
    public void AScreenThatOpensFromAnotherMakesTheirSwitchACut()
    {
        var scenes = SkinScenes.Parse(Song.Replace("""] } } }""", """], "opensFrom": ["entry"] } } }"""));
        Assert.Equal(["entry"], scenes.OpensFrom("song-select"));
        Assert.True(scenes.OpensFrom(FlowScenes.SongSelect, FlowScenes.Entry));
        Assert.False(scenes.OpensFrom(FlowScenes.SongSelect, FlowScenes.Result));
        Assert.False(SkinScenes.Parse(Song).OpensFrom(FlowScenes.SongSelect, FlowScenes.Entry));
    }

    [Fact]
    public void ABrokenFileFallsBackToTheNextFolder()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-skin-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "user"));
            Directory.CreateDirectory(Path.Combine(root, "bundled"));
            File.WriteAllText(Path.Combine(root, "user", SkinScenes.FileName), "{ broken");
            File.WriteAllText(Path.Combine(root, "bundled", SkinScenes.FileName), Song);
            var problems = new List<string>();
            var scenes = SkinScenes.Find([Path.Combine(root, "missing"), Path.Combine(root, "user"), Path.Combine(root, "bundled")], problems.Add);
            Assert.NotNull(scenes?.Scene("song-select"));
            Assert.Single(problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
