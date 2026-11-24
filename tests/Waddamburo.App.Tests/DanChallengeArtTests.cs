using Waddamburo.App.Presentation;

namespace Waddamburo.App.Tests;

public sealed class DanChallengeArtTests
{
    // A plate's name (black letters, white outline) inside the area becomes paper; the plate's black border outside stays.
    [Fact]
    public void PaintOutLeavesPaperAndBorder()
    {
        const int width = 20, height = 20;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var border = y < 2 || y >= 18;
                var letter = x is >= 8 and < 12 && y is >= 8 and < 12;
                var outline = !letter && x is >= 7 and < 13 && y is >= 7 and < 13;
                (byte r, byte g, byte b) = border || letter ? ((byte)0, (byte)0, (byte)0) : outline ? ((byte)255, (byte)255, (byte)255) : ((byte)230, (byte)210, (byte)160);
                rgba[(y * width + x) * 4] = r;
                rgba[(y * width + x) * 4 + 1] = g;
                rgba[(y * width + x) * 4 + 2] = b;
                rgba[(y * width + x) * 4 + 3] = 255;
            }
        DanChallengeArt.PaintOut(rgba, width, height, (.1f, .1f, .9f, .9f));
        for (var y = 2; y < 18; y++)
            for (var x = 2; x < 18; x++)
                Assert.Equal(230, rgba[(y * width + x) * 4]);
        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[(19 * width + 5) * 4]);
    }
}
