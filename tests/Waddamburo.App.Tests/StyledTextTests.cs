using Waddamburo.App.Cli;
using Waddamburo.Formats.Text;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Tests;

public sealed class StyledTextTests
{
    private static readonly string Font = BundledFonts.Extract(Path.Combine(Path.GetTempPath(), "waddamburo-test-fonts")).Title;

    // The player-entry title: shadow, black border, red border, white fill. Each layer shows where the others do not.
    private static readonly TextStyle Title = new("title", 64, [
        new(17, 4, 5, 6, 0x000000b0, 0x000000b0), TextLayer.Solid(0x101010ff, 15), TextLayer.Solid(0xe01818ff, 9),
        new(Top: 0xffffffff, Bottom: 0xc8c8d0ff)], LetterSpacing: -6, Slant: 0.12f, Align: TextAlign.Center, Fit: TextFit.Squeeze);

    private static (byte R, byte G, byte B, byte A) at(RgbaTextSurface surface, int x, int y)
    {
        var i = (y * (int)surface.Width + x) * 4;
        return (surface.Pixels[i], surface.Pixels[i + 1], surface.Pixels[i + 2], surface.Pixels[i + 3]);
    }

    [Fact]
    public void LayersStackFromTheBack()
    {
        var surface = NativeVerticalTextRasterizer.RenderStyled(Font, "ーー", Title, 1, 300, 150);
        // The bars' fill (white to grey), then red, then black, then the shadow fading out.
        var row = Enumerable.Range(0, 300 * 150).Select(i => at(surface, i % 300, i / 300)).ToArray();
        Assert.Contains(row, static p => p is { R: > 200, G: > 200, A: 255 });
        Assert.Contains(row, static p => p is { R: > 180, G: < 60, A: 255 });
        Assert.Contains(row, static p => p is { R: < 30, G: < 30, A: 255 });
        Assert.Equal(0, at(surface, 0, 0).A);
    }

    [Fact]
    public void EmptyTextIsTransparent() =>
        Assert.All(NativeVerticalTextRasterizer.RenderStyled(Font, "", Title, 1, 40, 20).Pixels, static b => Assert.Equal(0, b));

    // Squeezed text stays in its box; clipped text runs out of it.
    [Theory]
    [InlineData(TextFit.Squeeze, false)]
    [InlineData(TextFit.Shrink, false)]
    [InlineData(TextFit.Wrap, false)]
    [InlineData(TextFit.Clip, true)]
    public void FitKeepsLongTextInTheBox(TextFit fit, bool overflows)
    {
        var style = new TextStyle("plain", 30, [TextLayer.Solid(0xffffffff)], Fit: fit);
        var surface = NativeVerticalTextRasterizer.RenderStyled(Font, "A long line of text that cannot fit", style, 1, 120, 80);
        bool inked(int x) => Enumerable.Range(0, 80).Any(y => at(surface, x, y).A > 0);
        Assert.Equal(overflows, inked(119));
    }
}
