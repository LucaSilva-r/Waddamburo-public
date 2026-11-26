using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Flow;

/// <summary>
/// Which screens each screen can lead to: <see cref="Presentation.ScenePresenter"/> loads them in the background while
/// the game stands on it and keeps them from eviction (the screens after those too while there is room, on devices that
/// preload). A screen missing here only costs a load at its switch, as without this.
/// </summary>
internal static class ScreenGraph
{
    /// <summary>
    /// The screens reachable from <paramref name="from"/> within <paramref name="depth"/> switches, nearest first, with
    /// their distance (each screen once, at its nearest).
    /// </summary>
    public static IEnumerable<(SceneDefinition Scene, int Depth)> Reachable(SceneId from, GameShell shell, int depth)
    {
        var seen = new HashSet<SceneDefinition>();
        var visited = new HashSet<SceneId> { from };
        IEnumerable<SceneDefinition> level = Next(from, shell).ToArray();
        for (var distance = 1; distance <= depth && level.Any(); distance++)
        {
            var next = new List<SceneDefinition>();
            foreach (var scene in level)
            {
                if (!seen.Add(scene))
                    continue;
                yield return (scene, distance);
                if (visited.Add(scene.Id))
                    next.AddRange(Next(scene.Id, shell));
            }
            level = next;
        }
    }

    /// <summary>The screens <paramref name="from"/> leads to, the likeliest first.</summary>
    public static IEnumerable<SceneDefinition> Next(SceneId from, GameShell shell)
    {
        // Only what the frontend's catalog has: Waddamburo's has no attract loop, revival or game over.
        return next(from, shell).Where(scene => shell.Catalog.Contains(scene.Id)
            && (scene.Id != FlowScenes.GameOver && scene.Id != FlowScenes.RewardShop || !shell.Arcade.WaddamburoFrontend));
    }

    private static IEnumerable<SceneDefinition> next(SceneId from, GameShell shell)
    {
        var credit = shell.Credit;
        SceneDefinition initial(SceneId id) => shell.Catalog.Contains(id) ? shell.Catalog.GetDefinition(id) : new(SceneDefinition.CurrentVersion, id, []);
        SceneDefinition songSelect() => shell.Screens.SongSelect(sides(shell), credit.Waiwai, credit.AiBattle);
        IEnumerable<SceneDefinition> attract(SceneId next) => [initial(FlowScenes.Entry), initial(next)];
        // What a play goes through after its song: loaded before the song starts (nothing loads during one).
        IEnumerable<SceneDefinition> afterSong() =>
        [
            credit.AiBattle ? FlowScenes.AiResults : credit.Waiwai ? FlowScenes.WaiwaiResults : shell.Screens.Results,
            initial(FlowScenes.Retry),
            shell.Arcade.Home ? FlowScenes.NormalGameOver : FlowScenes.RewardGameOver,
            .. credit.AiBattle && !shell.Arcade.Home ? [initial(FlowScenes.AiRemind)] : Array.Empty<SceneDefinition>(),
        ];
        IEnumerable<SceneDefinition> afterDan() =>
            [shell.Screens.DaniResults, shell.Arcade.Home ? shell.Screens.DaniSelect(credit.PlayerSide) : FlowScenes.DaniGameOver];

        if (from == FlowScenes.Boot)
            return [initial(FlowScenes.Logo)];
        if (from == FlowScenes.Logo)
            return attract(FlowScenes.Title);
        if (from == FlowScenes.Title)
            return attract(FlowScenes.Caution);
        if (from == FlowScenes.Caution)
            return attract(FlowScenes.Movie);
        if (from == FlowScenes.Movie)
            return attract(FlowScenes.Logo);
        // Entry's mode select picks the credit's Song Select (they share most of their textures).
        if (from == FlowScenes.Entry)
            return [shell.Screens.SongSelect([0]), FlowScenes.RewardShopVisit, FlowScenes.SongSelectScene([0], waiwai: true),
                FlowScenes.SongSelectScene([0], aiBattle: true)];
        if (from == FlowScenes.SongSelect)
            return [gameplayParts(shell, dan: false), .. afterSong(),
                initial(credit.AiBattle ? FlowScenes.AiTutorial : credit.Waiwai ? FlowScenes.WaiwaiTutorial : FlowScenes.Tutorial),
                .. credit.AiBattle || credit.Waiwai ? Array.Empty<SceneDefinition>() : [shell.Screens.DaniSelect(credit.PlayerSide)],
                .. shell.Arcade.Home && !shell.Arcade.WaddamburoFrontend ? [FlowScenes.HomeRewards] : Array.Empty<SceneDefinition>()];
        if (from == FlowScenes.DaniSelect)
            return [gameplayParts(shell, dan: true), .. afterDan(), songSelect()];
        if (from == FlowScenes.Gameplay)
            return shell.Gameplay.Dan is not null ? [.. afterDan(), songSelect()] : [.. afterSong(), songSelect()];
        if (from == FlowScenes.Result)
            return shell.Dan.Result is not null ? [.. afterDan(), songSelect()] : [songSelect(), .. afterSong().Skip(1)];
        if (from == FlowScenes.Retry || from == FlowScenes.AiRemind)
            return [songSelect(), .. afterSong().Skip(2)];
        if (from == FlowScenes.GameOver)
            return [initial(FlowScenes.Logo)];
        if (from == FlowScenes.RewardShop || from == FlowScenes.Tutorial || from == FlowScenes.WaiwaiTutorial
            || from == FlowScenes.AiTutorial || from == FlowScenes.Calibration || from == FlowScenes.Benchmark)
            return [songSelect()];
        return [];
    }

    private static IReadOnlyList<int> sides(GameShell shell) => shell.Players.JoinedSides is { Count: > 0 } joined ? [.. joined] : [0];

    // A song's gameplay screen without its song's own parts (backgrounds and dancers: random or the song's skin, known
    // once a song is picked). Waiwai's and AI battle's layouts are fixed.
    private static SceneDefinition gameplayParts(GameShell shell, bool dan)
    {
        var joined = sides(shell);
        var scene = shell.Credit.AiBattle ? GameplaySceneComposition.CreateAiBattle(FlowScenes.Gameplay, shell.EnsoLayout)
            : shell.Credit.Waiwai ? GameplaySceneComposition.CreateWaiwai(FlowScenes.Gameplay, shell.EnsoLayout)
            : GameplaySceneComposition.Create(FlowScenes.Gameplay, new Random(0), shell.EnsoLayout, side: joined[0],
                twoPlayers: joined.Count > 1, dojo: dan);
        return new(SceneDefinition.CurrentVersion, scene.Id,
            scene.Layers.Where(static layer => !layer.ArchiveId.StartsWith("enso_original/", StringComparison.Ordinal)));
    }
}
