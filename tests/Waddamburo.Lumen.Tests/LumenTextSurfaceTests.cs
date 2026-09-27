using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenTextSurfaceTests
{
    [Fact]
    public void KeyCarriesTheFieldStyleAndText()
    {
        const string html = "<p align=\"right\"><font face=\"P_taiko_font\" size=\"20\" color=\"#12ab34\">名前五文字</font></p>";
        var key = LumenTextSurface.Key("あい:う", 138, 24, 20, LumenTextSurface.Align(html), LumenTextSurface.Colour(html));

        Assert.True(LumenTextSurface.TryParse(key, out var text, out var width, out var height, out var size, out var align, out var rgb));
        Assert.Equal(("あい:う", 138f, 24f, 20f, 'r', 0x12ab34U), (text, width, height, size, align, rgb));
        Assert.False(LumenTextSurface.TryParse(new LumenNativeSurfaceKey("song-title:x"), out _, out _, out _, out _, out _, out _));
    }
}
