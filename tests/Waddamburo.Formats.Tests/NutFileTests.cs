using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Tests;

public sealed class NutFileTests
{
    [Fact]
    public void ParsesVersionOneMultipleTexturesAndRetainsHeaders()
    {
        var data = createNut(1,
        [
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 0x00010001, 0xA1),
            new TextureFixture(NutPixelFormat.Bc3, 4, 4, 1, new byte[16], 0x00010002, 0xA2),
        ]);

        var nut = NutFile.Parse(data);

        Assert.Equal(1, nut.Version);
        Assert.Equal(2, nut.Textures.Length);
        Assert.Equal(0x00010001u, nut.Textures[0].GlobalId);
        Assert.Equal(NutPixelFormat.Bc3, nut.Textures[1].Format);
        Assert.Equal(0xA2, nut.Textures[1].HeaderBytes[0x0F]);
        Assert.Equal(16u, nut.Textures[1].DataSize);
        Assert.True(MemoryMarshal.TryGetArray(nut.Textures[1].Data, out var segment));
        Assert.Same(data, segment.Array);
    }

    [Fact]
    public void ParsesVersionTwoHeaderTableAndMipMetadata()
    {
        var data = createNut(2,
        [
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 2, new byte[16], 7, 0xB1),
            new TextureFixture(NutPixelFormat.Argb, 1, 1, 1, new byte[] { 128, 10, 20, 30 }, 8, 0xB2),
            new TextureFixture(NutPixelFormat.ArgbAlternate, 1, 1, 1, new byte[] { 255, 1, 2, 3 }, 9, 0xB3),
        ]);

        var nut = NutFile.Parse(data);

        Assert.Equal(2, nut.Version);
        Assert.Equal(3, nut.Textures.Length);
        Assert.Equal(2, nut.Textures[0].MipCount);
        Assert.Equal(7u, nut.Textures[0].GlobalId);
        Assert.Equal(new byte[] { 128, 10, 20, 30 }, nut.Textures[1].Data.ToArray());
        Assert.True(nut.Textures.All(texture => texture.DataOffset >= 0x10 + 3 * 0x50));
    }

    [Fact]
    public void MissingGidxIsPreservedAsUnknownIdentity()
    {
        var data = createNut(1,
            [new TextureFixture(NutPixelFormat.Argb, 1, 1, 1, new byte[4], null, 0xCC)]);

        var texture = Assert.Single(NutFile.Parse(data).Textures);

        Assert.Null(texture.GlobalId);
        Assert.Equal(0xCC, texture.HeaderBytes[0x0F]);
    }

    [Fact]
    public void EveryTruncationOfMinimalPackFailsPredictably()
    {
        var data = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0)]);

        for (var length = 0; length < data.Length; length++)
            Assert.ThrowsAny<FormatException>(() => NutFile.Parse(data.AsMemory(0, length)));
    }

    [Fact]
    public void RejectsUnsupportedVersionFormatAndShortHeader()
    {
        var version = createNut(1);
        version[4] = 3;
        Assert.Throws<FormatReadException>(() => NutFile.Parse(version));

        var format = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0)]);
        format[0x10 + 0x13] = 99;
        Assert.Throws<FormatReadException>(() => NutFile.Parse(format));

        var header = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0)]);
        writeUInt16(header.AsSpan(0x10 + 0x0C), 0x20);
        Assert.Throws<FormatReadException>(() => NutFile.Parse(header));
    }

    [Fact]
    public void RejectsInvalidDimensionsMipCountsAndPayloadSizes()
    {
        var zeroWidth = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0)]);
        writeUInt16(zeroWidth.AsSpan(0x10 + 0x14), 0);
        Assert.Throws<FormatReadException>(() => NutFile.Parse(zeroWidth));

        var excessiveMips = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0)]);
        excessiveMips[0x10 + 0x11] = 4;
        Assert.Throws<FormatReadException>(() => NutFile.Parse(excessiveMips));

        var shortMipChain = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc1, 4, 4, 2, new byte[16], 1, 0)]);
        writeUInt32(shortMipChain.AsSpan(0x10 + 8), 8);
        Assert.Throws<FormatReadException>(() => NutFile.Parse(shortMipChain));
    }

    [Fact]
    public void RejectsVersionTwoPayloadInsideHeaderTable()
    {
        var data = createNut(2,
        [
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0),
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 2, 0),
        ]);
        writeUInt32(data.AsSpan(0x10 + 0x20), 0x50);

        var exception = Assert.Throws<FormatReadException>(() => NutFile.Parse(data));
        Assert.Contains("header table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesTextureAndAllocationLimits()
    {
        var data = createNut(1,
            [new TextureFixture(NutPixelFormat.Bc3, 4, 4, 1, new byte[16], 1, 0)]);

        var textureLimit = Assert.Throws<FormatLimitException>(() =>
            NutFile.Parse(data, new ParserLimits(maxTextureBytes: 8)));
        Assert.Equal(nameof(ParserLimits.MaxTextureBytes), textureLimit.LimitName);

        var twoTextures = createNut(1,
        [
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 1, 0),
            new TextureFixture(NutPixelFormat.Bc1, 4, 4, 1, new byte[8], 2, 0),
        ]);
        Assert.Throws<FormatLimitException>(() =>
            NutFile.Parse(twoTextures, new ParserLimits(maxAllocationBytes: 8)));
    }

    [Theory]
    [InlineData(NutPixelFormat.Argb)]
    [InlineData(NutPixelFormat.ArgbAlternate)]
    public void RawArgbFormatsDecodeToRgba(NutPixelFormat format)
    {
        var data = createNut(1,
            [new TextureFixture(format, 1, 1, 1, new byte[] { 128, 10, 20, 30 }, 1, 0)]);
        var texture = Assert.Single(NutFile.Parse(data).Textures);

        var rgba = NutTextureDecoder.DecodeRgba8(texture);

        Assert.Equal(new byte[] { 10, 20, 30, 128 }, rgba);
    }

    private static byte[] createNut(byte version, TextureFixture[]? textures = null)
    {
        textures ??= [];
        const int headerSize = 0x50;
        var headers = new byte[textures.Length][];
        var payloadOffset = 0x10 + textures.Length * headerSize;
        for (var index = 0; index < textures.Length; index++)
        {
            var texture = textures[index];
            var header = headers[index] = new byte[headerSize];
            writeUInt32(header, checked((uint)(headerSize + texture.Data.Length)));
            writeUInt32(header.AsSpan(8), checked((uint)texture.Data.Length));
            writeUInt16(header.AsSpan(0x0C), headerSize);
            header[0x0F] = texture.UnknownByte;
            header[0x11] = texture.MipCount;
            header[0x13] = (byte)texture.Format;
            writeUInt16(header.AsSpan(0x14), texture.Width);
            writeUInt16(header.AsSpan(0x16), texture.Height);
            if (version == 2)
            {
                var absoluteHeaderOffset = 0x10 + index * headerSize;
                writeUInt32(header.AsSpan(0x20), checked((uint)(payloadOffset - absoluteHeaderOffset)));
            }

            "eXt\0"u8.CopyTo(header.AsSpan(0x30));
            if (texture.GlobalId is uint globalId)
            {
                "GIDX"u8.CopyTo(header.AsSpan(0x40));
                writeUInt32(header.AsSpan(0x48), globalId);
            }

            payloadOffset += texture.Data.Length;
        }

        using var stream = new MemoryStream();
        stream.Write("NTP3"u8);
        stream.WriteByte(version);
        stream.WriteByte(0);
        writeUInt16(stream, checked((ushort)textures.Length));
        stream.Write(new byte[8]);
        if (version == 1)
        {
            for (var index = 0; index < textures.Length; index++)
            {
                stream.Write(headers[index]);
                stream.Write(textures[index].Data);
            }
        }
        else
        {
            foreach (var header in headers)
                stream.Write(header);
            foreach (var texture in textures)
                stream.Write(texture.Data);
        }

        return stream.ToArray();
    }

    private static void writeUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        writeUInt16(buffer, value);
        stream.Write(buffer);
    }

    private static void writeUInt16(Span<byte> destination, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(destination, value);

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        writeUInt32(buffer, value);
        stream.Write(buffer);
    }

    private static void writeUInt32(Span<byte> destination, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    private sealed record TextureFixture(
        NutPixelFormat Format,
        ushort Width,
        ushort Height,
        byte MipCount,
        byte[] Data,
        uint? GlobalId,
        byte UnknownByte);
}
