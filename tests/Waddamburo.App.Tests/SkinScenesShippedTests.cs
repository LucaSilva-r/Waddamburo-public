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
