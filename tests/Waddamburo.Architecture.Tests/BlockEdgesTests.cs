using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Architecture.Tests;

// Atlas gutters: a block rewritten to repeat one column, row or texel of its neighbour.
public sealed class BlockEdgesTests
{
    // Texel t (row-major) gets index t % 4 in colour and t % 8 in alpha: every column, row and texel distinct enough.
    private static byte[] block(bool bc3)
    {
        var bytes = new byte[bc3 ? 16 : 8];
        var colour = bc3 ? 8 : 0;
        uint indices = 0;
        for (var texel = 0; texel < 16; texel++)
            indices |= (uint)((texel / 4 + texel) % 4) << (2 * texel);
        BitConverter.GetBytes(indices).CopyTo(bytes, colour + 4);
        if (bc3)
        {
            ulong alpha = 0;
            for (var texel = 0; texel < 16; texel++)
                alpha |= (ulong)(texel % 8) << (3 * texel);
            for (var i = 0; i < 6; i++)
                bytes[2 + i] = (byte)(alpha >> (8 * i));
        }
        return bytes;
    }

    private static int colourIndex(byte[] bytes, bool bc3, int x, int y) =>
        (int)(BitConverter.ToUInt32(bytes, (bc3 ? 8 : 0) + 4) >> (2 * (y * 4 + x))) & 3;

    private static int alphaIndex(byte[] bytes, int x, int y)
    {
        ulong alpha = 0;
        for (var i = 0; i < 6; i++)
            alpha |= (ulong)bytes[2 + i] << (8 * i);
        return (int)(alpha >> (3 * (y * 4 + x))) & 7;
    }

    [Theory]
    [InlineData(false, 3, -1)]
    [InlineData(false, -1, 0)]
    [InlineData(true, 0, -1)]
    [InlineData(true, 3, 3)]
    public void RepeatsTheEdge(bool bc3, int column, int row)
    {
        var original = block(bc3);
        var rewritten = (byte[])original.Clone();
        BlockEdges.Replicate(rewritten, bc3, column, row);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                int sx = column >= 0 ? column : x, sy = row >= 0 ? row : y;
                Assert.Equal(colourIndex(original, bc3, sx, sy), colourIndex(rewritten, bc3, x, y));
                if (bc3)
                    Assert.Equal(alphaIndex(original, sx, sy), alphaIndex(rewritten, x, y));
            }
        // Endpoints untouched.
        Assert.Equal(original.AsSpan(0, 2).ToArray(), rewritten.AsSpan(0, 2).ToArray());
        Assert.Equal(original.AsSpan(bc3 ? 8 : 0, 4).ToArray(), rewritten.AsSpan(bc3 ? 8 : 0, 4).ToArray());
    }
}
