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
        // Bars are looked at again every 8th frame.
        var full = frame(4, 4, (_, _) => ((byte)90, (byte)90, (byte)90));
        for (var shown = 0; shown < 8; shown++)
            mask.Apply(full = frame(4, 4, (_, _) => ((byte)90, (byte)90, (byte)90)));
        Assert.Equal(255, full[3]);
        var dark = frame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0));
        mask.Apply(dark);
        Assert.Equal(255, dark[3]);
    }

    // A black key (hologram videos): the background joined to the edge goes; a dark sleeve with a highlight that touches it
    // stays; a notch of pure black between the legs, which the hole filling closes, is cleared all the same.
    [Fact]
    public void KeyCutsTheBackgroundButNotDarkPartsOfTheFigure()
    {
        const int size = 96;
        var mask = new VideoMask(size, size, keyColor: 0x000000, keyTolerance: 40);
        var rgba = frame(size, size, (x, y) =>
            x is >= 43 and < 53 && y is >= 60 and < 88 ? ((byte)0, (byte)0, (byte)0) // between the legs
            : x is >= 24 and < 72 && y is >= 8 and < 88 ? ((byte)40, (byte)200, (byte)200) // Miku
            : x is >= 4 and < 24 && y is >= 32 and < 48 ? (y % 4 == 0 ? ((byte)120, (byte)120, (byte)120) : ((byte)25, (byte)25, (byte)25)) // sleeve
            : ((byte)0, (byte)0, (byte)0)); // background
        mask.Apply(rgba);
        int alpha(int x, int y) => rgba[(y * size + x) * 4 + 3];
        Assert.Equal(0, alpha(1, 1));
        Assert.Equal(255, alpha(48, 30));
        Assert.Equal(255, alpha(14, 41));
        Assert.Equal(0, alpha(48, 80));
    }

    // The luma key: black is gone, bright is solid, dark grey partly see-through, and over black every pixel looks as it was.
    [Fact]
    public void LumaKeyMakesBrightnessOpacity()
    {
        var mask = new VideoMask(3, 1, lumaCurve: 8);
        var rgba = frame(3, 1, (x, _) => x switch { 0 => ((byte)0, (byte)0, (byte)0), 1 => ((byte)25, (byte)20, (byte)10), _ => ((byte)40, (byte)200, (byte)220) });
        mask.Apply(rgba);
        Assert.Equal(0, rgba[3]);
        Assert.InRange(rgba[7], 120, 160);
        Assert.Equal(255, rgba[11]);
        Assert.InRange(rgba[4] * rgba[7] / 255, 23, 27); // over black: 25 again
    }
}
