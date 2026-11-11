using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Package;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor.Tests;

public sealed class MovieTimelineContextTests
{
    private static LumenPlayer player()
    {
        const string json = """
            { "format": 1, "root": "main", "sprites": {
              "main": { "frames": 4, "layers": [
                { "character": "child", "name": "child", "depth": 1, "until": 2, "keys": [{ "frame": 0 }] },
                { "character": "other", "name": "replacement", "depth": 1, "keys": [{ "frame": 2 }] }
              ] },
              "child": { "frames": 3, "layers": [
                { "character": "leaf", "name": "leaf", "keys": [{ "frame": 0 }] }
              ] },
              "leaf": { "frames": 5, "layers": [] },
              "other": { "frames": 2, "layers": [] }
            } }
            """;
        return new(LmbSemanticReader.Read(LmbFile.Parse(MoviePackage.Assemble(json).Lmb)).Value, 1280, 720);
    }

    [Fact]
    public void ChildPlaybackDoesNotChangeTheOpenedParentTimeline()
    {
        var movie = player();
        var context = new MovieTimelineContext();
        var child = movie.InspectDisplayList().Single(clip => clip.Name == "child");
        Assert.True(movie.InspectGoto(child.Path, 1, play: false));
        Assert.Equal("", context.Resolve(movie.InspectDisplayList())!.Path);
        Assert.Equal(0, context.Resolve(movie.InspectDisplayList())!.Frame);
        Assert.Equal(1, movie.InspectDisplayList().Single(clip => clip.Name == "child").Frame);
        context.Open(child.Path, child.CharacterId);
        Assert.Equal(1, context.Resolve(movie.InspectDisplayList())!.Frame);
        var leaf = movie.InspectDisplayList().Single(clip => clip.Name == "leaf");
        context.Open(leaf.Path, leaf.CharacterId);
        Assert.Equal(["", child.Path, leaf.Path], context.Ancestors());
        context.Parent();
        Assert.Equal(child.Path, context.Resolve(movie.InspectDisplayList())!.Path);
        context.Parent();
        Assert.Equal("", context.Resolve(movie.InspectDisplayList())!.Path);
    }

    [Fact]
    public void ReusedDepthDoesNotSilentlyOpenAnUnrelatedTimeline()
    {
        var movie = player();
        var child = movie.InspectDisplayList().Single(clip => clip.Name == "child");
        var context = new MovieTimelineContext();
        context.Open(child.Path, child.CharacterId);
        Assert.True(movie.InspectGoto("", 2, play: false));
        Assert.Contains(movie.InspectDisplayList(), clip => clip.Path == child.Path && clip.Name == "replacement");
        Assert.Null(context.Resolve(movie.InspectDisplayList()));
        Assert.Equal(child.Path, context.Path);
        context.Parent();
        Assert.Equal("", context.Resolve(movie.InspectDisplayList())!.Path);
    }
}
