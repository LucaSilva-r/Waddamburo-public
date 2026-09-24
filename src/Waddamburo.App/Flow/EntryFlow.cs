using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

/// <summary>
/// The entry (player join): plays its jingle; the movie itself requests Song Select, which the shell
/// applies once the last voice has finished.
/// </summary>
internal sealed class EntryFlow(GameShell shell) : FlowScene(shell)
{
    private readonly string? _jingle = shell.Options.JinglePath ?? shell.FindJingle("JINGLE_ENTRY.nub");
    private AudioPlaybackHandle? _bgm;

    public override IndicatorScene IndicatorsFor(SceneId scene) => IndicatorScene.Entry;

    public override void Enter(SceneId scene)
    {
        if (_jingle is null || Shell.Audio is not { } audio)
            return;
        // --play-jingle plays it once (a sync check); the scene itself loops it.
        _bgm = Shell.Options.JinglePath is null ? audio.PlayLoop(_jingle, AudioBus.Bgm) : audio.PlayOneShot(_jingle, AudioBus.Bgm);
    }

    public override void Exit(SceneId scene)
    {
        if (_bgm is { } bgm)
            Shell.Audio?.Mixer.Stop(bgm, TimeSpan.FromMilliseconds(20));
        _bgm = null;
    }

    public override void Advance(LumenInputSnapshot input)
    {
        Shell.Active.Player.Advance(input);
        Shell.Hosts.AdvanceEntry();
        Shell.DonRenderer?.Advance();
    }
}
