using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenPanelMorphTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    public void ButtonSlicesKeepCornersAndSharedUvsWhenTheNativeQuadIsReversed(bool reverse, float inset)
    {
        var a = new LumenRenderVertex(0, 0, 0, 0); var b = new LumenRenderVertex(206, 0, 1, 0);
        var c = new LumenRenderVertex(206, 98, 1, 1); var d = new LumenRenderVertex(0, 98, 0, 1);
        var quad = reverse ? new LumenRenderQuad(1, c, d, a, b, LumenRenderColor.White, LumenRenderColor.Transparent)
            : new LumenRenderQuad(1, a, b, c, d, LumenRenderColor.White, LumenRenderColor.Transparent);
        var slices = LumenPanelMorph.ResizeSlices(quad, new(400, 300, 92, 110), inset: inset).ToArray();
        Assert.Equal(9, slices.Length);
        Assert.Equal(12, slices[0].TopRight.X - slices[0].TopLeft.X);
        Assert.Equal(12, slices[0].BottomLeft.Y - slices[0].TopLeft.Y);
        Assert.Equal(492, slices[^1].BottomRight.X); Assert.Equal(410, slices[^1].BottomRight.Y);
        Assert.Equal(slices[0].TopRight, slices[1].TopLeft);
        Assert.Equal(slices[0].BottomLeft, slices[3].TopLeft);
        Assert.Equal((inset + 12) / 206, slices[0].TopRight.U, 4);
        Assert.All(slices, q => Assert.True(q.UseNearestSampling));
    }

    [Theory]
    [InlineData(360, 230, 506, 246)]
    [InlineData(300, 162, 653, 356.5f)]
    [InlineData(240, 95, 800, 467)]
    public void NativeCornersAndBordersRetainTheirPixelSizeAtEveryPanelSize(float x, float y, float width, float height)
    {
        var source = new LumenNativeSurfacePlacement(360, 230, 506, 246);
        var destination = new LumenNativeSurfacePlacement(x, y, width, height);
        foreach (var right in new[] { false, true })
            foreach (var bottom in new[] { false, true })
            {
                var edge = new LumenRenderVertex(source.X + (right ? source.Width : 0), source.Y + (bottom ? source.Height : 0), 0, 0);
                var inner = edge with { X = edge.X + (right ? -35.2f : 35.2f), Y = edge.Y + (bottom ? -32 : 32) };
                var a = LumenPanelMorph.Warp(edge, source, destination);
                var b = LumenPanelMorph.Warp(inner, source, destination);
                Assert.Equal(35.2f, Math.Abs(a.X - b.X), 3);
                Assert.Equal(32, Math.Abs(a.Y - b.Y), 3);
                Assert.Equal(x + (right ? width : 0), a.X);
                Assert.Equal(y + (bottom ? height : 0), a.Y);
            }
        var middle = LumenPanelMorph.Warp(new(source.X + source.Width / 2, source.Y + source.Height / 2, .5f, .5f), source, destination);
        Assert.Equal(x + width / 2, middle.X);
        Assert.Equal(y + height / 2, middle.Y);
        Assert.Equal(.5f, middle.U);
    }
}
