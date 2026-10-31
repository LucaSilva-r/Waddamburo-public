using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
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

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Result;

    public override void Enter(SceneId scene)
    {
        _gameOverEndTick = -1;
        if (scene != FlowScenes.Result)
            return;
        _resultStartTick = Shell.Tick;
        // The next song's Song Select loads in the background while the results play.
        if (Shell.Hosts.CreditNext == Waddamburo.Game.Lumen.TaikoCreditNext.NextSong)
            Shell.Prefetch(FlowScenes.SongSelectScene([.. Shell.JoinedSides], Shell.Hosts.Waiwai));
        // dani_result's song boards: fills song_name1 to 3, and a challenge's name art.
        if (Shell.Hosts.DanResult is not null && Shell.Active.Player.Layers[0].Player is { } dani)
        {
            if (Shell.Hosts.DanChallenge > 0)
                dani.SetNativeFill("challenge_short", DanChallengeArt.Key(Shell.Hosts.DanSeries, Shell.Hosts.DanChallenge, "short"));
            for (var index = 0; index < gameplay.CourseSongs.Count; index++)
            {
                var title = Shell.Titles.GetGameplayTitle(gameplay.CourseSongs[index]);
                _ = Shell.Titles.Resolve(title);
                dani.SetNativeFill($"song_name{index + 1}", title);
            }
        }
        // result.lm's song_name slot is the same as gameplay song_info's: reuse its title texture.
        var resultIndex = Shell.Active.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "result");
        if (resultIndex >= 0 && gameplay.Song is { } song)
            Shell.Active.Player.Layers[resultIndex].Player.SetNativeFill("song_name", Shell.Titles.GetGameplayTitle(song));
    }

    // Don-chan's 成績発表 (VO_RESULT 0, played as the song ends) is heard in silence; the music follows.
    // ponytail: the clip's 2.38 s as ticks; read the voice's playback state if banks ever differ.
    private const int MusicDelayTicks = 143;

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Result)
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

    public override void Tick(FlowInput input)
    {
        var scene = Shell.Active.Id;
        if (scene == FlowScenes.Result)
        {
            // The song's shutter stays over the results until their first frames are drawn.
            if (Shell.Overlay.IsShown && !Shell.FadePending && Shell.Tick - _resultStartTick >= 2)
                Shell.Overlay.Clear();
            if (Shell.Hosts.DanResult is not null)
                Shell.Hosts.DaniResult?.Poll();
            // ponytail: a course's results take a song's delay until a trace times theirs.
            if (Shell.Tick - _resultStartTick >= MusicDelayTicks) // StartMusic keeps a playing loop
                Shell.Sounds?.Results.StartMusic(waiwai: Shell.Hosts.WaiwaiResult is not null);
            if (Shell.FinishFade()) return;
            // The movie ends itself (_global.isAllEnd; 15-21 s traced by closing message; Waiwai's
            // reports AllFinish).
            if (input.Escape || (Shell.Hosts.WaiwaiResult is { } waiwai
                    ? waiwai.Ended
                    : RetryGameHostBinding.IsEnd(Shell.Active.Player.Layers[0].Player)))
                // A course ends the credit however many songs it had left (the coins inserted stay); at home,
                // the course list again.
                if (Shell.Hosts.DanResult is not null)
                {
                    if (!Shell.Arcade.Home)
                        Shell.Catalog.Replace(FlowScenes.DaniGameOver);
                    Shell.FadeTo(Shell.Arcade.Home ? FlowScenes.DaniSelect : FlowScenes.GameOver);
                }
                else
                Shell.FadeTo(Shell.Hosts.CreditNext switch
                {
                    TaikoCreditNext.Revival => FlowScenes.Retry,
                    TaikoCreditNext.NextSong => FlowScenes.SongSelect,
                    _ => FlowScenes.GameOver,
                });
        }
        else if (scene == FlowScenes.Retry)
        {
            if (Shell.FinishFade()) return;
            if (RetryGameHostBinding.IsEnd(Shell.Active.Player.Layers.Single().Player))
                Shell.FadeTo(Shell.Hosts.Retry?.Succeeded == true ? FlowScenes.SongSelect : FlowScenes.GameOver);
        }
        else if (Shell.Hosts.GameOver?.Ended == true)
        {
            if (_gameOverEndTick < 0)
                _gameOverEndTick = Shell.Tick;
            if (Shell.Tick - _gameOverEndTick < GameOverHoldTicks)
                return;
            // Traced: the credit's end returns to the attract loop at logo_namco.
            Shell.SongsPlayed = 0;
            Shell.Show(FlowScenes.Logo);
        }
    }
}
