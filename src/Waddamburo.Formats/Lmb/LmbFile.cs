using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Lmb;

/// <summary>Lossless structural view of an LMB header and ordered record stream.</summary>
public sealed class LmbFile
{
    public const int HeaderLength = 0x40;

    private readonly FrozenDictionary<uint, ImmutableArray<LmbRecord>> _recordsByTag;

    private LmbFile(
        int fileLength,
        ImmutableArray<byte> headerBytes,
        ImmutableArray<LmbRecord> records)
    {
        FileLength = fileLength;
        HeaderBytes = headerBytes;
        Records = records;
        _recordsByTag = records
            .GroupBy(record => record.Tag)
            .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
    }

    public int FileLength { get; }

    public ImmutableArray<byte> HeaderBytes { get; }

    public ImmutableArray<LmbRecord> Records { get; }

    public static LmbFile Parse(ReadOnlyMemory<byte> data, ParserLimits? limits = null)
    {
        var parserLimits = limits ?? ParserLimits.Default;
        if (data.Length > parserLimits.MaxFileBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxFileBytes),
                data.Length,
                parserLimits.MaxFileBytes,
                0);
        }
        if (data.Length < HeaderLength)
            throw new FormatReadException("LMB header is truncated", 0, HeaderLength, data.Length);
        if (!data.Span[..4].SequenceEqual("LMB\0"u8))
            throw new FormatReadException("Invalid LMB magic", 0, 4, 4);

        var records = ImmutableArray.CreateBuilder<LmbRecord>();
        var tagOccurrences = new Dictionary<uint, int>();
        long offset = HeaderLength;
        while (offset < data.Length)
        {
            if (records.Count >= parserLimits.MaxRecordCount)
            {
                throw new FormatLimitException(
                    nameof(ParserLimits.MaxRecordCount),
                    records.Count + 1L,
                    parserLimits.MaxRecordCount,
                    offset);
            }

            requireSpan(data, offset, 8, "LMB record header");
            var headerOffset = checked((int)offset);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(headerOffset, 4));
            var wordCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(headerOffset + 4, 4));
            var payloadLength = checked((long)wordCount * 4);
            var payloadOffset = checked(offset + 8);
            requireSpan(data, payloadOffset, payloadLength, $"LMB record 0x{tag:X8} payload");

            var occurrenceIndex = tagOccurrences.GetValueOrDefault(tag);
            tagOccurrences[tag] = occurrenceIndex + 1;
            records.Add(new LmbRecord(
                records.Count,
                occurrenceIndex,
                tag,
                wordCount,
                offset,
                payloadOffset,
                ImmutableArray.CreateRange(data.Span.Slice(headerOffset, 8).ToArray()),
                data.Slice(checked((int)payloadOffset), checked((int)payloadLength))));
            offset = checked(payloadOffset + payloadLength);
        }

        return new LmbFile(
            data.Length,
            ImmutableArray.CreateRange(data.Span[..HeaderLength].ToArray()),
            records.ToImmutable());
    }

    public ImmutableArray<LmbRecord> GetRecords(uint tag) =>
        _recordsByTag.GetValueOrDefault(tag, ImmutableArray<LmbRecord>.Empty);

    public LmbRecord GetSingleRecord(uint tag)
    {
        var records = GetRecords(tag);
        return records.Length == 1
            ? records[0]
            : throw new FormatException($"Expected one LMB record 0x{tag:X8}, found {records.Length}.");
    }

    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        destination.Write(HeaderBytes.AsSpan());
        foreach (var record in Records)
        {
            destination.Write(record.HeaderBytes.AsSpan());
            destination.Write(record.Payload.Span);
        }
    }

    private static void requireSpan(ReadOnlyMemory<byte> data, long offset, long length, string label)
    {
        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException($"{label} span overflows", offset, length, 0, exception);
        }

        if (offset < 0 || length < 0 || end > data.Length)
        {
            var available = offset >= 0 && offset <= data.Length ? data.Length - offset : 0;
            throw new FormatReadException($"{label} is truncated", Math.Max(0, offset), length, available);
        }
    }
}

public sealed record LmbRecord(
    int Index,
    int TagOccurrenceIndex,
    uint Tag,
    uint WordCount,
    long HeaderOffset,
    long PayloadOffset,
    ImmutableArray<byte> HeaderBytes,
    ReadOnlyMemory<byte> Payload)
{
    public long PayloadLength => checked((long)WordCount * 4);

    public long EndOffset => checked(PayloadOffset + PayloadLength);
}
