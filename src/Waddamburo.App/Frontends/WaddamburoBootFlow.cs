using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's boot: its boot movie, then straight to who's playing and the entry (no attract loop). Any key, drum hit
/// or button skips it (the function keys do not); it also ends by itself.
/// </summary>
internal sealed class WaddamburoBootFlow(GameShell shell) : FlowScene(shell)
{
    public override ScreenKind ScreenFor(SceneId scene) => ScreenKind.Boot;

    public override void Tick(FlowInput input)
    {
        if (Shell.Scenes.FinishFade())
            return;
        if (input.Skip || input.DrumSide is not null
            || input.Keys.Presses.Any(static press => press.Key is < SdlKeyboardKey.F1 or > SdlKeyboardKey.F8)
            || Shell.ScreenEnded)
            Shell.Scenes.Show(Shell.Home());
    }
}
