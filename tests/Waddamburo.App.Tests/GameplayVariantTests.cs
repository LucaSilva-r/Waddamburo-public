using Waddamburo.App.Frontends;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Tests;

public sealed class GameplayVariantTests
{
    // The skin editor's gameplay variants: the movies it shows (those a skin places, as the editor filters them).
    private static string[] shown(int side = 0, bool twoPlayers = false, bool dojo = false) =>
        [.. new WaddamburoScreens().Placed(FlowScenes.Gameplay, new Random(1), side, twoPlayers, dojo)
            .Where(static layer => GameplaySceneComposition.PlaceName(Path.GetFileNameWithoutExtension(layer.MovieId)) is not null)
            .Select(static layer => $"{layer.HostId} {Path.GetFileNameWithoutExtension(layer.MovieId)}")];

    [Fact]
    public void EachVariantShowsItsOwnLane()
    {
        var one = shown();
        Assert.NotEmpty(one);
        Assert.Contains(shown(twoPlayers: true), static layer => layer.StartsWith(GameplaySceneComposition.PlayerTwoHostId, StringComparison.Ordinal));
        Assert.NotEqual(one, shown(side: 1));
        Assert.NotEqual(one, shown(dojo: true));
    }
}
