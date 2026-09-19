namespace Waddamburo.Formats.IO;

/// <summary>Reports input that exceeds an explicit parser resource limit.</summary>
public sealed class FormatLimitException : FormatException
{
    public FormatLimitException(string limitName, long actual, long maximum, long offset)
        : base($"{limitName} limit exceeded at offset 0x{offset:X}: {actual} is greater than {maximum}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitName);
        ArgumentOutOfRangeException.ThrowIfNegative(actual);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        LimitName = limitName;
        Actual = actual;
        Maximum = maximum;
        Offset = offset;
    }

    public string LimitName { get; }

    public long Actual { get; }

    public long Maximum { get; }

    public long Offset { get; }
}
