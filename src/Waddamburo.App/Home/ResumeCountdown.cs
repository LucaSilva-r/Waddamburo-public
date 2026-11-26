using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Home;

/// <summary>
/// Home, both frontends: Resume in the pause menu counts down on the timer movie (time_counter, as the entry shows
/// it) before the song goes on, so the player is ready (as osu! does). Green's timer is driven by its callbacks, ours
/// reads timer.left; the caller gets each whole second to sound it as its frontend does.
/// </summary>
internal sealed class ResumeCountdown
{
    public const int Seconds = 3;
    private int _ticksLeft;

    public ResumeCountdown(LumenGameSceneInstance scene)
    {
        Scene = scene;
        // Its callbacks register on its first frame. A timer of ours reads timer.left instead.
        counter.Advance();
        if (counter.UsesProvider)
            return;
        counter.TryInvokeCallback("SetMusicNum", [LumenHostValue.FromNumber(0)]);
        counter.TryInvokeCallback("SetShadow", [LumenHostValue.FromBoolean(true)]);
    }

    public static SceneDefinition Definition(SceneId id) => new(SceneDefinition.CurrentVersion, id, [
        new SceneLayerDefinition("indicator/packeddata.ddp", "time_counter/time_counter.lm", LumenMatrix.Identity,
            LayerRoles.SystemIndicators),
    ]);

    public LumenGameSceneInstance Scene { get; }

    public bool Running => _ticksLeft > 0;

    private LumenPlayer counter => Scene.Player.Layers[0].Player;

    public void Start()
    {
        _ticksLeft = Seconds * 60;
        if (counter.UsesProvider)
        {
            counter.Publish("timer.left", LumenHostValue.FromNumber(Seconds));
            return;
        }
        counter.TryInvokeCallback("SetVisible", [LumenHostValue.FromBoolean(true)]);
        counter.TryInvokeCallback("Start", [LumenHostValue.FromNumber(Seconds), LumenHostValue.FromBoolean(true)]);
    }

    public void Cancel() => _ticksLeft = 0;

    /// <summary>One tick, telling <paramref name="secondsLeft"/> the whole seconds left; true on the tick the count reaches zero.</summary>
    public bool Advance(Action<int> secondsLeft)
    {
        if (_ticksLeft == 0) return false;
        counter.Advance();
        _ticksLeft--;
        if (counter.UsesProvider)
            counter.Publish("timer.left", _ticksLeft == 0 ? LumenHostValue.Undefined : LumenHostValue.FromNumber((_ticksLeft + 59) / 60));
        secondsLeft((_ticksLeft + 59) / 60);
        return _ticksLeft == 0;
    }

    // place: where the layout puts the timer (its "timer" element).
    public LumenRenderSnapshot CreateSnapshot(float interpolation, Func<LumenSceneLayer, LumenSceneLayer> place) =>
        new LumenScenePlayer(1280, 720, [place(Scene.Player.Layers[0])]).CreateRenderSnapshot(interpolation);
}
