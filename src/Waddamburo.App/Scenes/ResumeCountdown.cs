using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Scenes;

/// <summary>
/// Home: Resume in the pause menu counts down on the game's own timer movie (time_counter, as the
/// entry shows it) before the song goes on, so the player is ready (as osu! does). It ticks as the
/// game's menu timers do (<see cref="CountdownCues"/>).
/// </summary>
internal sealed class ResumeCountdown
{
    public const int Seconds = 3;
    private int _ticksLeft;
    private readonly CountdownCues _cues = new();

    public ResumeCountdown(LumenGameSceneInstance scene)
    {
        Scene = scene;
        // Its callbacks register on its first frame.
        counter.Advance();
        counter.TryInvokeCallback("SetMusicNum", [LumenHostValue.FromNumber(0)]);
        counter.TryInvokeCallback("SetShadow", [LumenHostValue.FromBoolean(true)]);
    }

    public static SceneDefinition Definition(SceneId id) => new(SceneDefinition.CurrentVersion, id, [
        new SceneLayerDefinition("indicator/packeddata.ddp", "time_counter/time_counter.lm", LumenMatrix.Identity,
            SystemIndicators.HostId),
    ]);

    public LumenGameSceneInstance Scene { get; }

    public bool Running => _ticksLeft > 0;

    private LumenPlayer counter => Scene.Player.Layers[0].Player;

    public void Start()
    {
        _ticksLeft = Seconds * 60;
        _cues.Reset();
        _cues.Advance(Seconds);
        counter.TryInvokeCallback("SetVisible", [LumenHostValue.FromBoolean(true)]);
        counter.TryInvokeCallback("Start", [LumenHostValue.FromNumber(Seconds), LumenHostValue.FromBoolean(true)]);
    }

    public void Cancel() => _ticksLeft = 0;

    /// <summary>One tick, playing the count's cues; true on the tick the count reaches zero.</summary>
    public bool Advance(Action<string, int> play)
    {
        if (_ticksLeft == 0) return false;
        counter.Advance();
        _ticksLeft--;
        foreach (var (bank, cue) in _cues.Advance((_ticksLeft + 59) / 60))
            play(bank, cue);
        return _ticksLeft == 0;
    }

    public LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        new LumenScenePlayer(1280, 720, [Scene.Player.Layers[0]]).CreateRenderSnapshot(interpolation);
}
