using Waddamburo.Formats.IO;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Tests;

public sealed class BlockCompressionDecoderTests
{
    [Fact]
    public void Bc1DecodesOpaqueRgb565EndpointToRgba()
    {
        var block = new byte[] { 0x00, 0xF8, 0, 0, 0, 0, 0, 0 };

        var rgba = BlockCompressionDecoder.DecodeBc1(block, 4, 4);

        Assert.Equal(64, rgba.Length);
        for (var pixel = 0; pixel < 16; pixel++)
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba.AsSpan(pixel * 4, 4).ToArray());
    }

    [Fact]
    public void Bc1ThreeColourModeDecodesTransparentSelector()
    {
        var block = new byte[] { 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        var rgba = BlockCompressionDecoder.DecodeBc1(block, 4, 4);

        Assert.All(rgba, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Bc3DecodesExplicitAlphaAndFourColourMode()
    {
        var block = new byte[16];
        block[0] = 255;
        block[1] = 0;
        block[8] = 0xE0;
        block[9] = 0x07;

        var rgba = BlockCompressionDecoder.DecodeBc3(block, 4, 4);

        for (var pixel = 0; pixel < 16; pixel++)
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, rgba.AsSpan(pixel * 4, 4).ToArray());
    }

    [Fact]
    public void Bc3UsesSixAlphaModeTerminalValues()
    {
        var block = new byte[16];
        block[2] = 7;
        block[8] = 0x1F;
        block[9] = 0;

        var rgba = BlockCompressionDecoder.DecodeBc3(block, 4, 4);

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[..4]);
        Assert.Equal(0, rgba[7]);
    }

    [Fact]
    public void DecoderCropsPartialEdgeBlocks()
    {
        var block = new byte[] { 0x00, 0xF8, 0, 0, 0, 0, 0, 0 };

        var rgba = BlockCompressionDecoder.DecodeBc1(block, 2, 2);

        Assert.Equal(16, rgba.Length);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
    }

    [Fact]
    public void DecoderRejectsTruncationAndOutputLimitViolations()
    {
        Assert.Throws<FormatReadException>(() =>
            BlockCompressionDecoder.DecodeBc3(new byte[15], 4, 4));

        var limit = Assert.Throws<FormatLimitException>(() =>
            BlockCompressionDecoder.DecodeBc1(
                new byte[8],
                4,
                4,
                new ParserLimits(maxAllocationBytes: 63)));
        Assert.Equal(nameof(ParserLimits.MaxAllocationBytes), limit.LimitName);
    }
}
