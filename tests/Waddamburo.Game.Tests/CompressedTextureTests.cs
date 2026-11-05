using Waddamburo.Formats.Nut;

namespace Waddamburo.Game.Tests;

public sealed class CompressedTextureTests
{
    // One green, fully opaque BC3 block (as BlockCompressionDecoderTests).
    private static byte[] greenBlock()
    {
        var block = new byte[16];
        block[0] = 255;
        block[8] = 0xE0;
        block[9] = 0x07;
        return block;
    }

    [Fact]
    public void AKeptTextureDecodesItsBlocksOnDemand()
    {
        var texture = new LumenTextureContent(0, 4, 4, []) { Blocks = greenBlock(), BlockFormat = LumenBlockFormat.Bc3 };

        Assert.Equal(BlockCompressionDecoder.DecodeBc3(greenBlock(), 4, 4), texture.Pixels());
        Assert.True(texture.Rgba8.IsEmpty);
    }

    [Fact]
    public void AnRgbaTextureGivesItsOwnPixels()
    {
        byte[] pixels = [1, 2, 3, 4];
        var texture = new LumenTextureContent(0, 1, 1, [.. pixels]);

        Assert.Equal(pixels, texture.Pixels());
    }
}
