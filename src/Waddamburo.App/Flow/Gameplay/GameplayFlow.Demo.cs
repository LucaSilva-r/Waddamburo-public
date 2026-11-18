using Waddamburo.App.Scenes;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow.Gameplay;

internal sealed partial class GameplayFlow
{
    /// <summary>The attract loop's demo play is on.</summary>
    public bool Demo => _mode is DemoPlay;

    /// <summary>
    /// The attract loop's demo play (between its CMs): a stock song on autoplay, unsaved, no banner or pause; it fades
    /// back to logo_namco after <see cref="Length"/>, and a drum, coin or card (takeOver) starts a credit.
    /// </summary>
    private sealed class DemoPlay(GameplayFlow flow, Func<FlowInput, bool> takeOver) : PlayMode(flow)
    {
        private static readonly TimeSpan Length = TimeSpan.FromSeconds(30);

        public override bool RealPlay => false;

        public override IndicatorScene Indicators => IndicatorScene.Demo;

        public override string? ModeLabel => Strings.T("mode.demo");

        // The demo drums like a person (15 hits a second), not a stress run.
        public override TimeSpan? AutoplayInterval => TimeSpan.FromSeconds(1 / 15d);

        public override bool EndBanner => false;

        public override bool UsesPlayerLayout => false;

        // The demo's bot drums alone: the players' keys only take over (#19).
        public override SdlKeyboardSnapshot PlayerKeys(SdlKeyboardSnapshot keys) => new([], null, keys.Timestamp);

        public override bool TakesTick(FlowInput input)
        {
            // Exit (on the switch either makes) ends the demo. Once it's fading out, a hit waits for the logo: taking over
            // then left the fade's black over entry.
            if (!Shell.Scenes.FadePending && takeOver(input) || Shell.Scenes.FinishFade())
                return true;
            // Escape ends the demo like its timer (it abandoned the song into Song Select, #19).
            if (!input.Escape && !(Flow.revealed && (Flow.chartTime() >= Length || Flow.ChartOver)))
                return false;
            if (Play.Music is { } music)
                Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromSeconds(1));
            Shell.Scenes.FadeTo(FlowScenes.Logo);
            return true;
        }

        public override void Exit()
        {
            if (Play.Music is { } music)
                Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
            Play.Music = null;
            Play.Clock.Reset();
            Flow._directStart = false;
            Play.Lanes.Stop();
            Play.Charts = [];
            Shell.Gameplay.Stop();
            Shell.PlayRequests.ClearActive();
            Flow._mode = new NormalPlay(Flow);
        }
    }
}
