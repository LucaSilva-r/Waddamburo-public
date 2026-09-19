using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Tests;

public sealed class LmbFileTests
{
    private static readonly uint[] SinglePayload = [0xAABBCCDD];

    [Fact]
    public void PreservesHeaderOrderingRepeatedTagsAndUnknownPayloadsLosslessly()
    {
        var data = createLmb(
            (0xF001, new uint[] { 1, 2 }),
            (0xDEAD, new uint[] { 3 }),
            (0x0001, Array.Empty<uint>()),
            (0xDEAD, new uint[] { 4, 5 }));
        data[0x20] = 0xA5;

        var lmb = LmbFile.Parse(data);

        Assert.Equal(0xA5, lmb.HeaderBytes[0x20]);
        Assert.Equal(new uint[] { 0xF001, 0xDEAD, 0x0001, 0xDEAD }, lmb.Records.Select(record => record.Tag));
        Assert.Equal(0, lmb.Records[1].TagOccurrenceIndex);
        Assert.Equal(1, lmb.Records[3].TagOccurrenceIndex);
        Assert.Equal(0x40, lmb.Records[0].HeaderOffset);
        Assert.Equal(0x48, lmb.Records[0].PayloadOffset);
        Assert.Equal(new byte[] { 0, 0, 0, 3 }, lmb.Records[1].Payload.ToArray());

        using var rebuilt = new MemoryStream();
        lmb.WriteTo(rebuilt);
        Assert.Equal(data, rebuilt.ToArray());
    }

    [Fact]
    public void PayloadsRemainZeroCopyViewsOfTheSource()
    {
        var data = createLmb((0x1234, SinglePayload));

        var record = Assert.Single(LmbFile.Parse(data).Records);

        Assert.True(MemoryMarshal.TryGetArray(record.Payload, out var segment));
        Assert.Same(data, segment.Array);
        Assert.Equal(0x48, segment.Offset);
    }

    [Fact]
    public void RejectsEveryHeaderAndRecordTruncation()
    {
        var data = createLmb((0x1234, new uint[] { 1, 2 }));

        for (var length = 0; length < LmbFile.HeaderLength; length++)
            Assert.Throws<FormatReadException>(() => LmbFile.Parse(data.AsMemory(0, length)));

        Assert.Empty(LmbFile.Parse(data.AsMemory(0, LmbFile.HeaderLength)).Records);
        for (var length = LmbFile.HeaderLength + 1; length < data.Length; length++)
            Assert.Throws<FormatReadException>(() => LmbFile.Parse(data.AsMemory(0, length)));
    }

    [Fact]
    public void RejectsInvalidMagicAndImpossibleWordCount()
    {
        var magic = createLmb();
        magic[0] = 0;
        Assert.Throws<FormatReadException>(() => LmbFile.Parse(magic));

        var count = createLmb((0x1234, Array.Empty<uint>()));
        BinaryPrimitives.WriteUInt32BigEndian(count.AsSpan(0x44, 4), uint.MaxValue);
        var exception = Assert.Throws<FormatReadException>(() => LmbFile.Parse(count));
        Assert.Equal(0x48, exception.Offset);
    }

    [Fact]
    public void EnforcesRecordLimitBeforeParsingAnotherHeader()
    {
        var data = createLmb(
            (1, Array.Empty<uint>()),
            (2, Array.Empty<uint>()));

        var exception = Assert.Throws<FormatLimitException>(() =>
            LmbFile.Parse(data, new ParserLimits(maxRecordCount: 1)));

        Assert.Equal(nameof(ParserLimits.MaxRecordCount), exception.LimitName);
        Assert.Equal(0x48, exception.Offset);
    }

    [Fact]
    public void TagLookupRetainsOccurrencesAndRejectsAmbiguousSingletons()
    {
        var lmb = LmbFile.Parse(createLmb(
            (0xAAAA, Array.Empty<uint>()),
            (0xBBBB, Array.Empty<uint>()),
            (0xAAAA, Array.Empty<uint>())));

        Assert.Equal(2, lmb.GetRecords(0xAAAA).Length);
        Assert.Same(lmb.Records[1], lmb.GetSingleRecord(0xBBBB));
        Assert.Throws<FormatException>(() => lmb.GetSingleRecord(0xAAAA));
        Assert.Throws<FormatException>(() => lmb.GetSingleRecord(0xCCCC));
    }

    private static byte[] createLmb(params (uint Tag, uint[] Words)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write("LMB\0"u8);
        stream.Write(new byte[LmbFile.HeaderLength - 4]);
        foreach (var (tag, words) in records)
        {
            writeUInt32(stream, tag);
            writeUInt32(stream, checked((uint)words.Length));
            foreach (var word in words)
                writeUInt32(stream, word);
        }

        return stream.ToArray();
    }

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
