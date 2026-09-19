using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Tests;

public sealed class BoundedBinaryReaderTests
{
    [Theory]
    [InlineData(ByteOrder.LittleEndian, 0x1234, 0x12345678u)]
    [InlineData(ByteOrder.BigEndian, 0x3412, 0x78563412u)]
    public void ReadsMemoryWithSelectedByteOrder(ByteOrder byteOrder, ushort expected16, uint expected32)
    {
        using var reader = new BoundedBinaryReader(
            new byte[] { 0x34, 0x12, 0x78, 0x56, 0x34, 0x12 },
            byteOrder,
            baseOffset: 0x200);

        Assert.Equal(expected16, reader.ReadUInt16());
        Assert.Equal(expected32, reader.ReadUInt32());
        Assert.Equal(0x206, reader.Offset);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ReadsStreamFromItsInitialPositionAndLeavesItOpen()
    {
        using var stream = new MemoryStream(new byte[] { 0xFF, 0x01, 0x02, 0x03 });
        stream.Position = 1;

        using (var reader = new BoundedBinaryReader(stream, ByteOrder.BigEndian, leaveOpen: true))
        {
            Assert.Equal(3, reader.Length);
            Assert.Equal(0x0102, reader.ReadUInt16());
            reader.Skip(1);
            Assert.Equal(3, reader.Position);
        }

        Assert.True(stream.CanRead);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void NonSeekableStreamHandlesFragmentedReadsAndReportsConsumedTruncation()
    {
        using var stream = new FragmentedNonSeekableStream(new byte[] { 0x78, 0x56, 0x34, 0x12, 0xAA });
        using var reader = new BoundedBinaryReader(stream, baseOffset: 0x100);

        Assert.Null(reader.Length);
        Assert.Equal(0x12345678u, reader.ReadUInt32());

        var exception = Assert.Throws<FormatReadException>(() => reader.ReadUInt16());
        Assert.Equal(0x104, exception.Offset);
        Assert.Equal(2, exception.RequestedLength);
        Assert.Equal(1, exception.AvailableLength);
        Assert.Equal(5, reader.Position);
    }

    [Fact]
    public void TruncationReportsAbsoluteOffsetAndDoesNotAdvanceMemoryReader()
    {
        using var reader = new BoundedBinaryReader(new byte[] { 0x01, 0x02, 0x03 }, baseOffset: 0x40);
        reader.ReadByte();

        var exception = Assert.Throws<FormatReadException>(() => reader.ReadUInt32());

        Assert.Equal(0x41, exception.Offset);
        Assert.Equal(4, exception.RequestedLength);
        Assert.Equal(2, exception.AvailableLength);
        Assert.Equal(1, reader.Position);
    }

    [Fact]
    public void AllocationLimitIsCheckedBeforeReadingOrAllocating()
    {
        var limits = new ParserLimits(maxAllocationBytes: 3);
        using var reader = new BoundedBinaryReader(new byte[8], limits: limits, baseOffset: 0x80);

        var exception = Assert.Throws<FormatLimitException>(() => reader.ReadBytes(4));

        Assert.Equal(nameof(ParserLimits.MaxAllocationBytes), exception.LimitName);
        Assert.Equal(4, exception.Actual);
        Assert.Equal(3, exception.Maximum);
        Assert.Equal(0x80, exception.Offset);
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void FileLimitRejectsMemoryAndSeekableStreamsAtConstruction()
    {
        var limits = new ParserLimits(maxFileBytes: 3);

        Assert.Throws<FormatLimitException>(() => new BoundedBinaryReader(new byte[4], limits: limits));
        Assert.Throws<FormatLimitException>(() =>
            new BoundedBinaryReader(new MemoryStream(new byte[4]), limits: limits));
    }

    [Fact]
    public void StringLimitAndStrictUtf8AreEnforced()
    {
        var limits = new ParserLimits(maxStringBytes: 2);
        using var limited = new BoundedBinaryReader(new byte[] { 1, 2, 3 }, limits: limits);
        Assert.Throws<FormatLimitException>(() => limited.ReadUtf8(3));
        Assert.Equal(0, limited.Position);

        using var invalid = new BoundedBinaryReader(new byte[] { 0xC3, 0x28 }, baseOffset: 0x10);
        var exception = Assert.Throws<FormatReadException>(() => invalid.ReadUtf8(2));
        Assert.Equal(0x10, exception.Offset);
    }

    [Fact]
    public void ScalarBitPatternsArePreserved()
    {
        using var reader = new BoundedBinaryReader(new byte[]
        {
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0xC0,
        });

        Assert.Equal(1.0f, reader.ReadSingle());
        Assert.Equal(-2.5, reader.ReadDouble());
    }

    [Fact]
    public void DisposedReaderRejectsFurtherReads()
    {
        var reader = new BoundedBinaryReader(new byte[] { 1 });
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    private sealed class FragmentedNonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
}
