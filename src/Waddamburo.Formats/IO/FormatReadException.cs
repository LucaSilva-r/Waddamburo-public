namespace Waddamburo.Formats.IO;

/// <summary>Reports a malformed or truncated read at an absolute asset offset.</summary>
public sealed class FormatReadException : FormatException
{
    public FormatReadException(
        string message,
        long offset,
        long requestedLength = 0,
        long availableLength = 0,
        Exception? innerException = null)
        : base($"{message} (offset 0x{offset:X}).", innerException)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);
        ArgumentOutOfRangeException.ThrowIfNegative(availableLength);

        Offset = offset;
        RequestedLength = requestedLength;
        AvailableLength = availableLength;
    }

    public long Offset { get; }

    public long RequestedLength { get; }

    public long AvailableLength { get; }
}
