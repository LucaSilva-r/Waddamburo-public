using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Nut;

/// <summary>Validated, source-backed NTP3 texture pack metadata and payload views.</summary>
public sealed class NutFile
{
    private const int FileHeaderLength = 0x10;
    private const int MinimumTextureHeaderLength = 0x24;

    private NutFile(
        byte version,
        ImmutableArray<byte> headerBytes,
        ImmutableArray<NutTexture> textures)
    {
        Version = version;
        HeaderBytes = headerBytes;
        Textures = textures;
    }

    public byte Version { get; }

    public ImmutableArray<byte> HeaderBytes { get; }

    public ImmutableArray<NutTexture> Textures { get; }

    public static NutFile Parse(ReadOnlyMemory<byte> data, ParserLimits? limits = null)
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

        requireSpan(data, 0, FileHeaderLength, "NTP3 file header");
        var source = data.Span;
        if (!source[..4].SequenceEqual("NTP3"u8))
            throw malformed("Invalid NTP3 magic", 0);

        var version = source[4];
        if (version is not (1 or 2))
            throw malformed($"Unsupported NTP3 version {version}", 4);

        var textureCount = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        if (textureCount > parserLimits.MaxRecordCount)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxRecordCount),
                textureCount,
                parserLimits.MaxRecordCount,
                6);
        }

        ensureBuilderAllocation(textureCount, parserLimits, 6);
        var textures = ImmutableArray.CreateBuilder<NutTexture>(textureCount);
        var headerOffset = FileHeaderLength;
        for (var index = 0; index < textureCount; index++)
        {
            requireSpan(data, headerOffset, MinimumTextureHeaderLength, "NTP3 texture header");
            source = data.Span;

            var totalSize = readUInt32(source, headerOffset);
            var dataSize = readUInt32(source, headerOffset + 8);
            var headerSize = readUInt16(source, headerOffset + 0x0C);
            if (headerSize < MinimumTextureHeaderLength)
                throw malformed("NTP3 texture header is too short", headerOffset + 0x0C);
            requireSpan(data, headerOffset, headerSize, "NTP3 texture header");
            if (totalSize < headerSize)
                throw malformed("NTP3 texture total size is smaller than its header", headerOffset);

            var mipCount = source[headerOffset + 0x11];
            var formatValue = source[headerOffset + 0x13];
            if (!Enum.IsDefined((NutPixelFormat)formatValue))
                throw malformed($"Unsupported NTP3 texture format {formatValue}", headerOffset + 0x13);

            var format = (NutPixelFormat)formatValue;
            var width = readUInt16(source, headerOffset + 0x14);
            var height = readUInt16(source, headerOffset + 0x16);
            validateDimensions(width, height, mipCount, parserLimits, headerOffset);

            if (dataSize > parserLimits.MaxTextureBytes)
            {
                throw new FormatLimitException(
                    nameof(ParserLimits.MaxTextureBytes),
                    dataSize,
                    parserLimits.MaxTextureBytes,
                    headerOffset + 8);
            }

            var requiredDataSize = calculateMipDataSize(format, width, height, mipCount, headerOffset);
            if (dataSize < requiredDataSize)
            {
                throw new FormatReadException(
                    "NTP3 texture payload is too short for its dimensions and mip count",
                    headerOffset + 8,
                    requiredDataSize,
                    dataSize);
            }

            long payloadOffset;
            long nextHeaderOffset;
            if (version == 1)
            {
                payloadOffset = checkedAdd(headerOffset, headerSize, headerOffset, "NTP3 v1 payload offset");
                var minimumTotalSize = checked((long)headerSize + dataSize);
                if (totalSize < minimumTotalSize)
                {
                    throw new FormatReadException(
                        "NTP3 v1 total size does not contain its texture payload",
                        headerOffset,
                        minimumTotalSize,
                        totalSize);
                }

                nextHeaderOffset = checkedAdd(headerOffset, totalSize, headerOffset, "NTP3 v1 next header");
            }
            else
            {
                var relativeDataOffset = readUInt32(source, headerOffset + 0x20);
                if (relativeDataOffset < headerSize)
                    throw malformed("NTP3 v2 payload overlaps its texture header", headerOffset + 0x20);
                payloadOffset = checkedAdd(headerOffset, relativeDataOffset, headerOffset, "NTP3 v2 payload offset");
                nextHeaderOffset = checkedAdd(headerOffset, headerSize, headerOffset, "NTP3 v2 next header");
            }

            requireSpan(data, payloadOffset, dataSize, "NTP3 texture payload");
            uint? globalId = null;
            if (headerSize >= 0x10)
            {
                var globalIdOffset = checked(headerOffset + headerSize - 0x10);
                if (data.Span.Slice(globalIdOffset, 4).SequenceEqual("GIDX"u8))
                    globalId = readUInt32(data.Span, globalIdOffset + 8);
            }

            var rawHeader = ImmutableArray.CreateRange(data.Span.Slice(headerOffset, headerSize).ToArray());
            textures.Add(new NutTexture(
                index,
                totalSize,
                headerSize,
                dataSize,
                mipCount,
                format,
                width,
                height,
                globalId,
                headerOffset,
                payloadOffset,
                rawHeader,
                data.Slice(checked((int)payloadOffset), checked((int)dataSize))));

            headerOffset = checked((int)nextHeaderOffset);
        }

        if (version == 2)
        {
            var headerTableEnd = headerOffset;
            var orderedPayloads = textures
                .OrderBy(texture => texture.DataOffset)
                .ToArray();
            long previousEnd = headerTableEnd;
            foreach (var texture in orderedPayloads)
            {
                if (texture.DataOffset < headerTableEnd)
                    throw malformed("NTP3 v2 payload overlaps the header table", texture.DataOffset);
                if (texture.DataOffset < previousEnd)
                    throw malformed("NTP3 v2 texture payloads overlap", texture.DataOffset);
                previousEnd = checkedAdd(
                    texture.DataOffset,
                    texture.DataSize,
                    texture.DataOffset,
                    "NTP3 v2 payload end");
            }
        }

        return new NutFile(
            version,
            ImmutableArray.CreateRange(data.Span[..FileHeaderLength].ToArray()),
            textures.MoveToImmutable());
    }

    private static long calculateMipDataSize(
        NutPixelFormat format,
        int width,
        int height,
        int mipCount,
        long offset)
    {
        long total = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            long levelSize = format switch
            {
                NutPixelFormat.Bc1 => checked((long)((mipWidth + 3) / 4) * ((mipHeight + 3) / 4) * 8),
                NutPixelFormat.Bc3 => checked((long)((mipWidth + 3) / 4) * ((mipHeight + 3) / 4) * 16),
                NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate => checked((long)mipWidth * mipHeight * 4),
                _ => throw new UnreachableException(),
            };
            total = checkedAdd(total, levelSize, offset, "NTP3 mip payload size");
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return total;
    }

    private static void validateDimensions(
        int width,
        int height,
        int mipCount,
        ParserLimits limits,
        long offset)
    {
        if (width == 0 || height == 0)
            throw malformed("NTP3 texture dimensions must be nonzero", offset + 0x14);
        if (width > limits.MaxTextureDimension)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureDimension),
                width,
                limits.MaxTextureDimension,
                offset + 0x14);
        }
        if (height > limits.MaxTextureDimension)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureDimension),
                height,
                limits.MaxTextureDimension,
                offset + 0x16);
        }
        if (mipCount == 0)
            throw malformed("NTP3 texture must contain at least one mip level", offset + 0x11);

        var maximumMipCount = 1;
        for (var maximumDimension = Math.Max(width, height); maximumDimension > 1; maximumDimension /= 2)
            maximumMipCount++;
        if (mipCount > maximumMipCount)
            throw malformed("NTP3 mip count exceeds the texture dimensions", offset + 0x11);
    }

    private static void ensureBuilderAllocation(int count, ParserLimits limits, long offset)
    {
        var referenceBytes = checked((long)count * IntPtr.Size);
        if (referenceBytes > limits.MaxAllocationBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxAllocationBytes),
                referenceBytes,
                limits.MaxAllocationBytes,
                offset);
        }
    }

    private static ushort readUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, sizeof(ushort)));

    private static uint readUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)));

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

    private static long checkedAdd(long left, long right, long offset, string label)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException($"{label} overflows", offset, innerException: exception);
        }
    }

    private static FormatReadException malformed(string message, long offset) => new(message, offset);
}

public enum NutPixelFormat : byte
{
    Bc1 = 0,
    Bc3 = 2,
    Argb = 14,
    ArgbAlternate = 17,
}

public sealed record NutTexture(
    int Index,
    uint TotalSize,
    ushort HeaderSize,
    uint DataSize,
    byte MipCount,
    NutPixelFormat Format,
    ushort Width,
    ushort Height,
    uint? GlobalId,
    long HeaderOffset,
    long DataOffset,
    ImmutableArray<byte> HeaderBytes,
    ReadOnlyMemory<byte> Data);
