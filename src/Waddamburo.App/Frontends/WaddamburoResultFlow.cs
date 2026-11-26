using Waddamburo.App.Flow;
using Waddamburo.App.Flow.Gameplay;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's results: the song's plays (or the course's totals) told to our results movie, its music, and on
/// result.continue (or Escape) the next song: Song Select, or the course list after a course. Home only: no revival,
/// game over, medals or credit end.
/// </summary>
internal sealed class WaddamburoResultFlow(GameShell shell, GameplayFlow gameplay) : FlowScene(shell)
{
    // Our results music starts once the results are up.
    private const int MusicDelayTicks = 60;
    private long _startTick;
    private bool _continued;

    public override ScreenKind ScreenFor(SceneId scene) => ScreenKind.Result;

    public override void Enter(SceneId scene)
    {
        _startTick = Shell.Tick;
        _continued = false;
        var movies = Shell.Active.Player.Layers.Select(static layer => layer.Player).ToArray();
        new WdRequests("result").Handle("result.continue", _ =>
        {
            if (_continued) return "the results are already ending";
            _continued = true;
            return null;
        }).Install(movies);
        if (Shell.Dan.Result is { } dan)
        {
            string?[] titles = [.. gameplay.CourseSongs.Select(static song => song.Descriptor.Title.English ?? song.Descriptor.Title.Primary)];
            foreach (var movie in movies)
                ResultNames.PublishDan(movie, dan, Shell.Credit.PlayerSide + 1, titles);
            Shell.Scenes.Prefetch(Shell.Screens.DaniSelect(Shell.Credit.PlayerSide));
        }
        else
        {
            var plays = Shell.PlayResults();
            foreach (var movie in movies)
                ResultNames.Publish(movie, plays, Shell.Credit);
            Shell.Scenes.Prefetch(Shell.Screens.SongSelect(Shell.Players.SongSelectSides));
        }
    }

    public override void Tick(FlowInput input)
    {
        // The song's shutter stays over the results until their first frames are drawn.
        if (Shell.Overlay.IsShown && !Shell.Scenes.FadePending && Shell.Tick - _startTick >= 2)
            Shell.Overlay.Clear();
        Shell.Hosts.Poll(FlowScenes.Result);
        if (Shell.Tick - _startTick >= MusicDelayTicks) // StartScreenMusic keeps a playing loop
            Shell.Sounds?.StartScreenMusic("music.result");
        if (Shell.Scenes.FinishFade())
            return;
        if (input.Escape || _continued)
            Shell.Scenes.FadeTo(Shell.Dan.Result is not null ? FlowScenes.DaniSelect : FlowScenes.SongSelect);
    }

    public override void Exit(SceneId scene)
    {
        Shell.Hosts.Left(scene);
        Shell.Sounds?.StopScreenMusic();
        Shell.Dan.Result = null;
    }
}
