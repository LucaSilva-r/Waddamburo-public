using Waddamburo.Skins.Green.Art;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Skins.Green;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// After a song: the results (normal or Waiwai), then per the credit's rules the next song, the
/// revival drum roll, or game over and back to the attract loop. Traced: each hands over under the
/// intermission fade, except results -> revival.
/// </summary>
internal sealed class CreditEndFlow(GameShell shell, GameplayFlow gameplay) : FlowScene(shell)
{
    private int _resultStartTick;
    // When the game over's movie terminated (-1: not yet); its last frame, after the "Produced by" credits
    // fade, is held while a real cabinet loads the attract (at least 4 s recorded; the recomp traces load at once).
    private int _gameOverEndTick = -1;
    private const int GameOverHoldTicks = 240;

    // ponytail: 30 s for the farewell (the traced player hit the drum after 33 s; no timer was seen).
    private const int RemindTimeoutTicks = 30 * 60;
    private long _remindStartTick;

    public override ScreenKind ScreenFor(SceneId scene) =>
        scene == FlowScenes.Retry ? ScreenKind.Retry : scene == FlowScenes.GameOver ? ScreenKind.GameOver : ScreenKind.Result;

    public override void Enter(SceneId scene)
    {
        _gameOverEndTick = -1;
        _remindStartTick = Shell.Tick;
        if (scene == FlowScenes.AiRemind)
            Shell.Sounds?.Results.StartRemindMusic();
        // Screens of ours ask the engine (WdNames scopes retry and gameover); the game's end themselves.
        _revival = null;
        _ending = false;
        _revivalEndedAt = -1;
        if (scene == FlowScenes.Retry && ours() is { } retry)
        {
            _revival = new RevivalChallenge();
            new WdRequests("retry").Handle("revival.done", _ => _revival?.Succeeded is null ? "the drum roll is still on" : end()).Install(retry);
        }
        else if (scene == FlowScenes.GameOver && ours() is { } over)
            new WdRequests("gameover").Handle("gameover.done", _ => end()).Install(over);
        if (scene != FlowScenes.Result)
            return;
        _resultStartTick = Shell.Tick;
        _continued = false;
        // The next song's Song Select loads in the background while the results play.
        if (Shell.Hosts.CreditNext == Waddamburo.Game.Flow.TaikoCreditNext.NextSong)
            Shell.Scenes.Prefetch(FlowScenes.SongSelectScene([.. Shell.Players.JoinedSides], Shell.Hosts.Waiwai, Shell.Hosts.AiBattle));
        // The game's results show the song titles in slots.
        if (Shell.Hosts.DanResult is not null)
        {
            var titles = gameplay.CourseSongs.Select(Shell.Titles.GetGameplayTitle).ToArray();
            foreach (var title in titles)
                _ = Shell.Titles.Resolve(title);
            DaniResultHostBinding.FillBoards(Shell.Active.Player.Layers[0].Player, titles, Shell.Hosts.DanChallenge > 0
                ? DanChallengeArt.Key(Shell.Hosts.DanSeries, Shell.Hosts.DanChallenge, "short") : null);
        }
        if (gameplay.Song is { } song)
            ResultHostBinding.FillTitle(Shell.Active, Shell.Titles.GetGameplayTitle(song));
        if (Shell.Hosts.DanResult is { } dan && ours() is { } movies)
            publishDanResult(dan, movies);
    }

    private RevivalChallenge? _revival;
    private long _revivalEndedAt = -1;
    private bool _ending;
    // ponytail: a screen of ours that never asks revival.done still moves on this long after the outcome.
    private const int RevivalOutcomeTicks = 5 * 60;

    // The scene's movies when they are ours (they read the provider), else null.
    private LumenPlayer[]? ours()
    {
        var movies = Shell.Active.Player.Layers.Select(static layer => layer.Player).ToArray();
        return movies.Any(static movie => movie.UsesProvider) ? movies : null;
    }

    private string? end()
    {
        if (_ending) return "the screen is already ending";
        _ending = true;
        return null;
    }

    // A course's results for a results movie of ours: the course's totals under the player's drum, its outcome, the
    // requirements where the course ended, and each song's row.
    private void publishDanResult(Waddamburo.Game.Dan.DanCourseResult dan, IEnumerable<LumenPlayer> movies)
    {
        var drum = Shell.Hosts.PlayerSide + 1;
        var total = dan.Total;
        static LumenHostValue n(double value) => LumenHostValue.FromNumber(value);
        foreach (var movie in movies.Where(static movie => movie.UsesProvider))
        {
            movie.Publish("dan.name", LumenHostValue.FromString(dan.Course.Name));
            movie.Publish("dan.passed", LumenHostValue.FromString(dan.Level switch { 2 => "gold", 1 => "red", _ => "failed" }));
            movie.Publish("dan.songsPlayed", n(dan.SongsPlayed));
            movie.Publish("result.score", n(total.Score), drum);
            movie.Publish("result.good", n(total.Goods), drum);
            movie.Publish("result.ok", n(total.Oks), drum);
            movie.Publish("result.bad", n(total.Bads), drum);
            movie.Publish("result.maxCombo", n(total.MaxCombo), drum);
            movie.Publish("result.rolls", n(total.Rolls), drum);
            movie.Publish("result.gauge", n(total.GaugePercent), drum);
            movie.Publish("result.cleared", LumenHostValue.FromBoolean(dan.Level > 0), drum);
            var requirements = dan.Course.Requirements;
            movie.Publish("dan.exam.count", n(requirements.Count));
            for (var index = 0; index < requirements.Count; index++)
            {
                var requirement = requirements[index];
                movie.PublishItem("dan.exam.type", index, LumenHostValue.FromString(requirement.Type));
                movie.PublishItem("dan.exam.target", index, requirement.Red is [var red, ..] ? n(red) : LumenHostValue.Undefined);
                movie.PublishItem("dan.exam.gold", index, n(requirement.Gold is [var gold, ..] ? gold : 0));
                movie.PublishItem("dan.exam.value", index, n(Waddamburo.Game.Dan.DanExams.ValueOf(requirement.Type, total)));
            }
            for (var index = 0; index < dan.Songs.Count; index++)
            {
                var counts = dan.Songs[index].Counts;
                var title = index < gameplay.CourseSongs.Count ? gameplay.CourseSongs[index].Descriptor.Title : null;
                movie.PublishItem("dan.songTitle", index, title is null ? LumenHostValue.Undefined : LumenHostValue.FromString(title.English ?? title.Primary));
                movie.PublishItem("dan.songGood", index, index < dan.SongsPlayed ? n(counts.Goods) : LumenHostValue.Undefined);
                movie.PublishItem("dan.songOk", index, index < dan.SongsPlayed ? n(counts.Oks) : LumenHostValue.Undefined);
                movie.PublishItem("dan.songBad", index, index < dan.SongsPlayed ? n(counts.Bads) : LumenHostValue.Undefined);
            }
        }
    }

    // Don-chan's 成績発表 (VO_RESULT 0, played as the song ends) is heard in silence; the music follows.
    // ponytail: the clip's 2.38 s as ticks; read the voice's playback state if banks ever differ.
    private const int MusicDelayTicks = 143;

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Result || scene == FlowScenes.AiRemind)
        {
            Shell.Sounds?.Results.StopMusic();
            Shell.Hosts.DanResult = null;
        }
        else if (scene == FlowScenes.GameOver)
        {
            Shell.Sounds?.GameOver.StopGameOverMusic();
            Shell.Catalog.Replace(FlowScenes.NormalGameOver);
        }
    }

    // The revival keeps the left player's movie setup (see RetryGameHostBinding), so a right-drum
    // player's Z/X/C/V drive it as D/F/J/K; the left drum is not theirs.
    public override SdlKeyboardSnapshot MapKeys(SdlKeyboardSnapshot keys)
    {
        if (Shell.Active.Id != FlowScenes.Retry || Shell.Hosts.PlayerSide != 1)
            return keys;
        static SdlKeyboardKey? toLeftDrum(SdlKeyboardKey key) => key switch
        {
            SdlKeyboardKey.Z => SdlKeyboardKey.D,
            SdlKeyboardKey.X => SdlKeyboardKey.F,
            SdlKeyboardKey.C => SdlKeyboardKey.J,
            SdlKeyboardKey.V => SdlKeyboardKey.K,
            SdlKeyboardKey.D or SdlKeyboardKey.F or SdlKeyboardKey.J or SdlKeyboardKey.K => null,
            _ => key,
        };
        return new SdlKeyboardSnapshot(
            keys.PressedKeys.Select(toLeftDrum).OfType<SdlKeyboardKey>(),
            keys.Presses.Where(press => toLeftDrum(press.Key) is not null)
                .Select(press => press with { Key = toLeftDrum(press.Key)!.Value }),
            keys.Timestamp);
    }

    // The results' requests (WdNames scope result), for a results movie of ours; the game's ends itself (IScreenEnds).
    private bool _continued;
    private LumenPlayer[] _asking = [];

    public override void Tick(FlowInput input)
    {
        var scene = Shell.Active.Id;
        if (scene == FlowScenes.Result)
        {
            var movies = Shell.Active.Player.Layers.Select(static layer => layer.Player).ToArray();
            if (!movies.SequenceEqual(_asking))
            {
                new WdRequests("result").Handle("result.continue", _ =>
                {
                    if (_continued) return "the results are already ending";
                    _continued = true;
                    return null;
                }).Install(movies);
                _asking = movies;
            }
            // The song's shutter stays over the results until their first frames are drawn.
            if (Shell.Overlay.IsShown && !Shell.Scenes.FadePending && Shell.Tick - _resultStartTick >= 2)
                Shell.Overlay.Clear();
            if (Shell.Hosts.DanResult is not null)
                Shell.Hosts.DaniResult?.Poll();
            Shell.Hosts.AiSectionResult?.Poll();
            // ponytail: a course's results take a song's delay until a trace times theirs.
            if (Shell.Tick - _resultStartTick >= MusicDelayTicks) // StartMusic keeps a playing loop
                Shell.Sounds?.Results.StartMusic(waiwai: Shell.Hosts.WaiwaiResult is not null, aiBattle: Shell.Hosts.AiOutcome is not null);
            if (Shell.Scenes.FinishFade()) return;
            // The movie ends itself (15-21 s traced by closing message), or ours asks.
            if (input.Escape || _continued || Shell.ScreenEnded)
                // A course ends the credit however many songs it had left (the coins inserted stay); at home,
                // the course list again.
                if (Shell.Hosts.DanResult is not null)
                {
                    if (!Shell.Arcade.Home)
                        Shell.Catalog.Replace(FlowScenes.DaniGameOver);
                    Shell.Scenes.FadeTo(Shell.Arcade.Home ? FlowScenes.DaniSelect : FlowScenes.GameOver);
                }
                else
                Shell.Scenes.FadeTo(Shell.Hosts.CreditNext switch
                {
                    TaikoCreditNext.Revival => FlowScenes.Retry,
                    TaikoCreditNext.NextSong => FlowScenes.SongSelect,
                    // An AI battle credit says goodbye with the player's rank and progress first (traced remind).
                    _ when Shell.Hosts.AiOutcome is not null && !Shell.Arcade.Home => FlowScenes.AiRemind,
                    _ => FlowScenes.GameOver,
                });
        }
        else if (scene == FlowScenes.AiRemind)
        {
            if (Shell.Scenes.FinishFade()) return;
            Shell.Hosts.AiRemind?.Poll();
            // The movie waits for a don; the cabinet doesn't wait forever.
            if (Shell.Tick - _remindStartTick == RemindTimeoutTicks)
                Shell.Hosts.AiRemind?.Exit();
            if (input.Escape || Shell.ScreenEnded)
                Shell.Scenes.FadeTo(FlowScenes.GameOver);
        }
        else if (scene == FlowScenes.Retry)
        {
            if (Shell.Scenes.FinishFade()) return;
            if (_revival is { } revival)
            {
                tickRevival(revival, input);
                return;
            }
            if (Shell.ScreenEnded)
                Shell.Scenes.FadeTo(Shell.Hosts.Retry?.Succeeded == true ? FlowScenes.SongSelect : FlowScenes.GameOver);
        }
        else if (_ending || Shell.ScreenEnded)
        {
            if (_gameOverEndTick < 0)
                _gameOverEndTick = Shell.Tick;
            // Ours says when it is over itself: no last frame held.
            if (!_ending && Shell.Tick - _gameOverEndTick < GameOverHoldTicks)
                return;
            // Traced: the credit's end returns to the attract loop at logo_namco.
            Shell.Players.SongsPlayed = 0;
            Shell.Scenes.Show(FlowScenes.Logo);
        }
    }

    // The revival drum roll on a screen of ours: the engine counts the player's drum (the keys are the left drum's
    // here: MapKeys), tells the screen, and moves on once the screen has shown the outcome.
    private void tickRevival(RevivalChallenge revival, FlowInput input)
    {
        var hits = 0;
        for (var pad = 0; pad < 4; pad++)
            if (input.Keys.IsDown(SdlInputBindings.Pads[pad]))
                hits++;
        revival.Tick(hits);
        static LumenHostValue n(double? value) => value is { } some ? LumenHostValue.FromNumber(some) : LumenHostValue.Undefined;
        foreach (var movie in Shell.Active.Player.Layers.Select(static layer => layer.Player).Where(static movie => movie.UsesProvider))
        {
            movie.Publish("revival.norma", n(revival.Norma));
            movie.Publish("revival.hits", n(revival.Hits));
            movie.Publish("revival.timeLeft", n(revival.TimeLeft));
            movie.Publish("revival.result", revival.Succeeded is { } won ? LumenHostValue.FromString(won ? "success" : "fail") : LumenHostValue.Undefined);
        }
        if (revival.Succeeded is not { } succeeded)
            return;
        if (_revivalEndedAt < 0)
            _revivalEndedAt = Shell.Tick;
        if (_ending || Shell.Tick - _revivalEndedAt >= RevivalOutcomeTicks)
            Shell.Scenes.FadeTo(succeeded ? FlowScenes.SongSelect : FlowScenes.GameOver);
    }
}
