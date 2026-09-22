namespace Waddamburo.Game.Scenes;

public enum RainbowTransitionState
{
    Idle,
    WaitingToCover,
    Covering,
    Covered,
    Revealing,
    Complete,
}

/// <summary>
/// Deterministic timing and authored-animation state for a rainbow scene handoff.
/// Loading and rendering remain owned by the composition root.
/// </summary>
public sealed class RainbowTransitionSequence
{
    public const int DefaultLeadInTicks = 60;
    public const int DefaultCoveredTicks = 120;

    private readonly int _leadInTicks;
    private readonly int _coveredTicks;
    private int _deadlineTick;

    public RainbowTransitionSequence(
        int leadInTicks = DefaultLeadInTicks,
        int coveredTicks = DefaultCoveredTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leadInTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(coveredTicks);
        _leadInTicks = leadInTicks;
        _coveredTicks = coveredTicks;
    }

    public RainbowTransitionState State { get; private set; }

    public void Begin(int currentTick)
    {
        if (State is not RainbowTransitionState.Idle and not RainbowTransitionState.Complete)
            throw new InvalidOperationException($"A rainbow transition is already {State}.");
        _deadlineTick = checked(currentTick + _leadInTicks);
        State = RainbowTransitionState.WaitingToCover;
    }

    public bool ShouldStartCover(int currentTick) =>
        State == RainbowTransitionState.WaitingToCover && currentTick >= _deadlineTick;

    public void StartCover()
    {
        require(RainbowTransitionState.WaitingToCover);
        State = RainbowTransitionState.Covering;
    }

    public bool FinishCoverWhenStopped(bool animationIsPlaying, int currentTick)
    {
        if (State != RainbowTransitionState.Covering || animationIsPlaying)
            return false;
        _deadlineTick = checked(currentTick + _coveredTicks);
        State = RainbowTransitionState.Covered;
        return true;
    }

    public bool ShouldStartReveal(int currentTick) =>
        State == RainbowTransitionState.Covered && currentTick >= _deadlineTick;

    public void StartReveal()
    {
        require(RainbowTransitionState.Covered);
        State = RainbowTransitionState.Revealing;
    }

    public bool FinishRevealWhenStopped(bool animationIsPlaying)
    {
        if (State != RainbowTransitionState.Revealing || animationIsPlaying)
            return false;
        State = RainbowTransitionState.Complete;
        return true;
    }

    private void require(RainbowTransitionState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Rainbow transition is {State}; expected {expected}.");
    }
}
