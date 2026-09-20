namespace Waddamburo.Platform.Sdl.Timing;

public readonly record struct FixedStepUpdate(int ExecutedTicks, int DroppedTicks);

/// <summary>Converts elapsed wall time into bounded fixed-rate simulation ticks.</summary>
public sealed class FixedStepAccumulator
{
    private double _accumulatedTicks;

    public FixedStepAccumulator(double ticksPerSecond = 60, int maximumCatchUpTicks = 5)
    {
        if (!double.IsFinite(ticksPerSecond) || ticksPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCatchUpTicks);
        TicksPerSecond = ticksPerSecond;
        MaximumCatchUpTicks = maximumCatchUpTicks;
    }

    public double TicksPerSecond { get; }

    public int MaximumCatchUpTicks { get; }

    public double InterpolationFraction => _accumulatedTicks - Math.Floor(_accumulatedTicks);

    public FixedStepUpdate AddElapsed(TimeSpan elapsed, Action tick, int remainingTickLimit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingTickLimit);

        _accumulatedTicks += elapsed.TotalSeconds * TicksPerSecond;
        var pending = checked((int)Math.Min(Math.Floor(_accumulatedTicks), int.MaxValue));
        if (pending == 0 || remainingTickLimit == 0)
            return default;

        var relevantPending = Math.Min(pending, remainingTickLimit);
        var executed = Math.Min(relevantPending, MaximumCatchUpTicks);
        var dropped = remainingTickLimit < pending
            ? 0
            : pending - executed;
        _accumulatedTicks -= executed + dropped;
        for (var index = 0; index < executed; index++)
            tick();
        return new FixedStepUpdate(executed, dropped);
    }
}
