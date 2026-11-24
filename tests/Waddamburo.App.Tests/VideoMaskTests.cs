using Waddamburo.App.Presentation;

namespace Waddamburo.App.Tests;

public sealed class VideoMaskTests
{
    private static byte[] frame(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                (rgba[(y * width + x) * 4], rgba[(y * width + x) * 4 + 1], rgba[(y * width + x) * 4 + 2], rgba[(y * width + x) * 4 + 3])
                    = (colour(x, y).R, colour(x, y).G, colour(x, y).B, 255);
        return rgba;
    }

    // A black top band (osu!taiko's) is see-through until a frame shows picture there; picture stays opaque, dark or not.
    [Fact]
    public void BlackBarsTurnTransparentUntilTheyShowPicture()
    {
        var mask = new VideoMask(4, 4);
        var letterboxed = frame(4, 4, (_, y) => y < 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)200, (byte)10, (byte)10));
        mask.Apply(letterboxed);
        Assert.Equal(0, letterboxed[3]);
        Assert.Equal(255, letterboxed[(3 * 4) * 4 + 3]);
        var full = frame(4, 4, (_, _) => ((byte)90, (byte)90, (byte)90));
        mask.Apply(full);
        Assert.Equal(255, full[3]);
        var dark = frame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0));
        mask.Apply(dark);
        Assert.Equal(255, dark[3]);
    }

    // A black key (hologram videos): the background joined to the edge goes, a dark detail inside the figure stays, and pure
    // black inside it (a gap between the arms) goes too.
    [Fact]
    public void KeyCutsTheBackgroundButNotDarkDetailInside()
    {
        const int size = 12;
        var mask = new VideoMask(size, size, keyColor: 0x000000, keyTolerance: 40);
        var rgba = frame(size, size, (x, y) =>
            x is < 2 or >= 10 || y is < 2 or >= 10 ? ((byte)0, (byte)0, (byte)0) // background
            : x is >= 4 and < 6 && y is >= 4 and < 8 ? ((byte)20, (byte)20, (byte)20) // dark stocking, inside
            : x is >= 7 and < 9 && y is >= 4 and < 8 ? ((byte)0, (byte)0, (byte)0) // gap between the arms
            : ((byte)40, (byte)200, (byte)200)); // Miku
        mask.Apply(rgba);
        int alpha(int x, int y) => rgba[(y * size + x) * 4 + 3];
        Assert.Equal(0, alpha(0, 0));
        Assert.Equal(255, alpha(5, 5));
        Assert.Equal(0, alpha(7, 5) < 128 ? 0 : 255);
        Assert.Equal(255, alpha(3, 9) > 0 ? 255 : 0);
    }
}
