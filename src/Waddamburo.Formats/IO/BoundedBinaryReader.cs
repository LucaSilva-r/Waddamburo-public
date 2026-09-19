using System.Buffers.Binary;
using System.Text;

namespace Waddamburo.Formats.IO;

/// <summary>
/// Reads endian-aware values from memory or a stream while enforcing absolute
/// offsets, truncation checks, and parser allocation limits.
/// </summary>
public sealed class BoundedBinaryReader : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly Stream? _stream;
    private readonly bool _isMemory;
    private readonly bool _leaveOpen;
    private readonly long _streamStart;
    private bool _disposed;

    public BoundedBinaryReader(
        ReadOnlyMemory<byte> data,
        ByteOrder byteOrder = ByteOrder.LittleEndian,
        ParserLimits? limits = null,
        long baseOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        validateByteOrder(byteOrder);

        Limits = limits ?? ParserLimits.Default;
        ensureWithinLimit(nameof(ParserLimits.MaxFileBytes), data.Length, Limits.MaxFileBytes, baseOffset);
        _memory = data;
        _isMemory = true;
        ByteOrder = byteOrder;
        BaseOffset = baseOffset;
        Length = data.Length;
    }

    public BoundedBinaryReader(
        Stream stream,
        ByteOrder byteOrder = ByteOrder.LittleEndian,
        ParserLimits? limits = null,
        long baseOffset = 0,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        validateByteOrder(byteOrder);
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));

        Limits = limits ?? ParserLimits.Default;
        _stream = stream;
        _leaveOpen = leaveOpen;
        ByteOrder = byteOrder;
        BaseOffset = baseOffset;

        if (stream.CanSeek)
        {
            _streamStart = stream.Position;
            var remaining = checked(stream.Length - _streamStart);
            ensureWithinLimit(nameof(ParserLimits.MaxFileBytes), remaining, Limits.MaxFileBytes, baseOffset);
            Length = remaining;
        }
    }

    public ByteOrder ByteOrder { get; }

    public ParserLimits Limits { get; }

    public long BaseOffset { get; }

    public long Position { get; private set; }

    public long Offset => checked(BaseOffset + Position);

    public long? Length { get; }

    public long? Remaining => Length is long length ? length - Position : null;

    public byte ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        readExactly(value);
        return value[0];
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    public ushort ReadUInt16()
    {
        Span<byte> value = stackalloc byte[sizeof(ushort)];
        readExactly(value);
        return ByteOrder == ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public uint ReadUInt32()
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        readExactly(value);
        return ByteOrder == ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    public long ReadInt64() => unchecked((long)ReadUInt64());

    public ulong ReadUInt64()
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        readExactly(value);
        return ByteOrder == ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(value)
            : BinaryPrimitives.ReadUInt64BigEndian(value);
    }

    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ensureWithinLimit(nameof(ParserLimits.MaxAllocationBytes), count, Limits.MaxAllocationBytes, Offset);

        var result = new byte[count];
        readExactly(result);
        return result;
    }

    public string ReadUtf8(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ensureWithinLimit(nameof(ParserLimits.MaxStringBytes), byteCount, Limits.MaxStringBytes, Offset);

        var startOffset = Offset;
        var bytes = ReadBytes(byteCount);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatReadException("Invalid UTF-8 string", startOffset, byteCount, byteCount, exception);
        }
    }

    public void Skip(long count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var startOffset = Offset;
        ensureReadableCount(count, startOffset);
        if (count == 0)
            return;

        if (_isMemory)
        {
            Position += count;
            return;
        }

        if (_stream!.CanSeek)
        {
            _stream.Position = checked(_streamStart + Position + count);
            Position += count;
            return;
        }

        Span<byte> buffer = stackalloc byte[4096];
        var remaining = count;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(remaining, buffer.Length);
            var read = _stream.Read(buffer[..requested]);
            if (read == 0)
                throw truncated(startOffset, count, count - remaining);
            Position += read;
            remaining -= read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_leaveOpen)
            _stream?.Dispose();
    }

    private void readExactly(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var startOffset = Offset;
        ensureReadableCount(destination.Length, startOffset);
        if (destination.IsEmpty)
            return;

        if (_isMemory)
        {
            _memory.Span.Slice(checked((int)Position), destination.Length).CopyTo(destination);
            Position += destination.Length;
            return;
        }

        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = _stream!.Read(destination[totalRead..]);
            if (read == 0)
                throw truncated(startOffset, destination.Length, totalRead);
            totalRead += read;
            Position += read;
        }
    }

    private void ensureReadableCount(long count, long startOffset)
    {
        long endPosition;
        try
        {
            endPosition = checked(Position + count);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException("Read range overflows the supported offset space", startOffset, count, 0, exception);
        }

        ensureWithinLimit(nameof(ParserLimits.MaxFileBytes), endPosition, Limits.MaxFileBytes, startOffset);
        if (Length is long length && endPosition > length)
            throw truncated(startOffset, count, Math.Max(0, length - Position));
    }

    private static FormatReadException truncated(long offset, long requested, long available) =>
        new("Unexpected end of input", offset, requested, available);

    private static void ensureWithinLimit(string name, long actual, long maximum, long offset)
    {
        if (actual > maximum)
            throw new FormatLimitException(name, actual, maximum, offset);
    }

    private static void validateByteOrder(ByteOrder byteOrder)
    {
        if (!Enum.IsDefined(byteOrder))
            throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unknown byte order.");
    }
}
