using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;

/// <summary>One tick's input as the scenes see it.</summary>
/// <param name="Keys">The keyboard after scene remapping.</param>
/// <param name="DrumSide">The drum hit this tick: 0 left (D/F/J/K), 1 right (Z/X/C/V).</param>
/// <param name="Skip">Space: skips the boot screens.</param>
/// <param name="Escape">Escape went down this tick.</param>
internal readonly record struct FlowInput(SdlKeyboardSnapshot Keys, int? DrumSide, bool Skip, bool Escape);

/// <summary>
/// The game-side behaviour of one or more scenes: what happens as they are shown and left, each
/// tick, and what they draw. The shell calls these for the active scene only.
/// </summary>
internal abstract class FlowScene(GameShell shell)
{
    protected GameShell Shell { get; } = shell;

    /// <summary>The system indicators' mode while <paramref name="scene"/> is shown.</summary>
    public abstract IndicatorScene IndicatorsFor(SceneId scene);

    /// <summary>The scene was just loaded and is now active.</summary>
    public virtual void Enter(SceneId scene)
    {
    }

    /// <summary>The scene is about to be replaced.</summary>
    public virtual void Exit(SceneId scene)
    {
    }

    /// <summary>Remaps the keyboard before the scene sees it.</summary>
    public virtual SdlKeyboardSnapshot MapKeys(SdlKeyboardSnapshot keys) => keys;

    /// <summary>Advances the scene's movies by one tick.</summary>
    public virtual void Advance(LumenInputSnapshot input)
    {
        Shell.Active.Player.Advance(input);
        Shell.DonRenderer?.Advance();
    }

    /// <summary>The scene's own logic, once the movies have advanced (skipped while a transition is pending).</summary>
    public virtual void Tick(FlowInput input)
    {
    }

    /// <summary>Per display frame (live input reaches only this callback).</summary>
    public virtual void UpdateFrame(SdlKeyboardSnapshot keys)
    {
    }

    public virtual LumenRenderSnapshot CreateSnapshot(float interpolation) =>
        Shell.Active.Player.CreateRenderSnapshot(interpolation);
}
