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
internal sealed class CreditEndFlow(GameShell shell) : FlowScene(shell)
{
    private int _resultStartTick;

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Result;

    public override void Enter(SceneId scene)
    {
        if (scene != FlowScenes.Result)
            return;
        _resultStartTick = Shell.Tick;
        Shell.Sounds?.Results.StartMusic(waiwai: Shell.Hosts.WaiwaiResult is not null);
    }

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Result)
            Shell.Sounds?.Results.StopMusic();
        else if (scene == FlowScenes.GameOver)
            Shell.Sounds?.GameOver.StopGameOverMusic();
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
            if (Shell.FinishFade()) return;
            // The movie ends itself (_global.isAllEnd; 15-21 s traced by closing message; Waiwai's
            // reports AllFinish).
            if (input.Escape || (Shell.Hosts.WaiwaiResult is { } waiwai
                    ? waiwai.Ended
                    : RetryGameHostBinding.IsEnd(Shell.Active.Player.Layers[0].Player)))
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
            // Traced: the credit's end returns to the attract loop at logo_namco.
            Shell.SongsPlayed = 0;
            Shell.Show(FlowScenes.Logo);
        }
    }
}
