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
    // Traced: in_extra 1027-1029 ms after NotifyEndCourseSelect (normal Song Select), 1514-1522 ms in
    // Waiwai's (sessions 9-12, 17).
    public const int DefaultLeadInTicks = 60;
    public const int WaiwaiLeadInTicks = 91;
    // The title is held stopped (the movie waits on frame 65) before out_extra: ~1.45 s in a real-time
    // capture of Green (in_extra at ~0.38 s, identical frames 1.5-2.8 s, out_extra's fade-out at ~2.83 s).
    // The TaikoRecomp traces show only ~0.1 s, but their loads finish early and the capture is the
    // reference (user-confirmed). A longer load holds longer.
    public const int DefaultCoveredTicks = 87;

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

    public void Begin(int currentTick, int? leadInTicks = null)
    {
        if (State is not RainbowTransitionState.Idle and not RainbowTransitionState.Complete)
            throw new InvalidOperationException($"A rainbow transition is already {State}.");
        _deadlineTick = checked(currentTick + (leadInTicks ?? _leadInTicks));
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
