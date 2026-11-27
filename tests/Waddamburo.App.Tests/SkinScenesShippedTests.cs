using Waddamburo.App.Frontends;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Tests;

public sealed class SkinScenesShippedTests
{
    private static string Movies([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "movies"));

    private static string[] Describe(SceneDefinition scene) =>
        [scene.Id.ToString()!, .. scene.Layers.Select(static layer => $"{layer.ArchiveId} {layer.MovieId} {layer.HostId} {layer.Transform.X},{layer.Transform.Y}")];

    // The skin's scenes.json was written from the built-in screens: until a skin changes it, the screens are the same.
    [Fact]
    public void TheShippedScenesAreTheBuiltInScreens()
    {
        var scenes = SkinScenes.Find([Movies()]);
        Assert.NotNull(scenes);
        var builtIn = new WaddamburoScreens();
        var skinned = new WaddamburoScreens { Skin = scenes };
        foreach (var screens in new Func<WaddamburoScreens, SceneDefinition>[]
        {
            static screens => screens.SongSelect([0]), static screens => screens.SongSelect([1, 0]), static screens => screens.DaniSelect(1),
            static screens => screens.Results, static screens => screens.DaniResults, static screens => screens.Rainbow,
            static screens => screens.Shutter, static screens => screens.Fade, static screens => screens.DanCover,
            static screens => screens.DanExitCover, static screens => screens.DanShutter,
        })
            Assert.Equal(Describe(screens(builtIn)), Describe(screens(skinned)));
        Assert.Equal(builtIn.Initial([]).SelectMany(Describe), skinned.Initial([]).SelectMany(Describe));
    }
}

public sealed class SkinPlaceTests
{
    // An ensolayout.bin as Green's: every point at (0, 184).
    private static byte[] greenLike()
    {
        const string signature = "serialization::archive";
        var data = new byte[0x35 + 28 * 12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data, (uint)signature.Length);
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(data, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x31), 28);
        for (var index = 0; index < 28; index++)
        {
            var record = data.AsSpan(0x35 + index * 12, 12);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(record, index);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(record[4..], 0);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(record[8..], 184);
        }
        return data;
    }

    // A place moves that movie alone: the lane moves, its gauge stays at the layout's place; the second lane keeps its own.
    [Fact]
    public void ThePlacesMoveEachGameplayMovieOnItsOwn()
    {
        var screens = new WaddamburoScreens
        {
            Skin = SkinScenes.Parse("""{ "format": 1, "scenes": { "gameplay": { "layers": [], "places": { "lane": { "x": 300, "y": 280, "rotation": 5 } } } } }"""),
        };
        var layout = Waddamburo.Formats.Layout.EnsoLayout.Standard;
        var scene = screens.Gameplay(Waddamburo.Game.Scenes.FlowScenes.Gameplay, new Random(1), layout);
        var lane = scene.Layers.Single(static layer => layer.MovieId == "lane/lane.lm");
        Assert.Equal((300f, 280f), (lane.Transform.X, lane.Transform.Y));
        Assert.NotEqual(0, lane.Transform.M12);
        var gauge = scene.Layers.First(static layer => layer.MovieId.StartsWith("gage_don_1p_", StringComparison.Ordinal));
        Assert.Equal((layout[14].X, layout[14].Y), (gauge.Transform.X, gauge.Transform.Y));
        // The game data's own ensolayout.bin (Green's puts the lane's point at 0, 184) moves nothing of ours: our movies
        // are made for the standard layout.
        var green = Waddamburo.Formats.Layout.EnsoLayout.Parse(greenLike());
        var onGreen = screens.Gameplay(Waddamburo.Game.Scenes.FlowScenes.Gameplay, new Random(1), green);
        var greenGauge = onGreen.Layers.First(static layer => layer.MovieId.StartsWith("gage_don_1p_", StringComparison.Ordinal));
        Assert.Equal((layout[14].X, layout[14].Y), (greenGauge.Transform.X, greenGauge.Transform.Y));
        var two = screens.Gameplay(Waddamburo.Game.Scenes.FlowScenes.Gameplay, new Random(1), layout, twoPlayers: true);
        Assert.Single(two.Layers, static layer => layer.MovieId == "lane/lane.lm" && layer.Transform.X == 300);
    }
}
