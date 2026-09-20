namespace Waddamburo.Lumen.Runtime;

public sealed record LumenRuntimeLimits
{
    public static LumenRuntimeLimits Default { get; } = new();

    public LumenRuntimeLimits(
        int maxInstructionsPerAction = 10_000,
        int maxStackValues = 4_096,
        int maxPendingActions = 4_096)
    {
        MaxInstructionsPerAction = requirePositive(maxInstructionsPerAction, nameof(maxInstructionsPerAction));
        MaxStackValues = requirePositive(maxStackValues, nameof(maxStackValues));
        MaxPendingActions = requirePositive(maxPendingActions, nameof(maxPendingActions));
    }

    public int MaxInstructionsPerAction { get; }

    public int MaxStackValues { get; }

    public int MaxPendingActions { get; }

    private static int requirePositive(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }
}
