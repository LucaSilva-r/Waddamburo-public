using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenVectorGeometryTests
{
    [Fact]
    public void FrameKeepsLightAboveAndLeftAndDarkBelowAndRight()
    {
        var paint = new LumenNativeSurfaceKey("paint");
        var box = new LumenNativeSurfacePlacement(10, 10, 120, 80);
        var frame = LumenVectorGeometry.Frame(box, paint);
        Assert.Equal(box, LumenNativeFrame.Bounds(frame[1]));
        var visible = frame.Where(q => q.NativeSurface != LumenVectorGeometry.Shadow
            && q.NativeSurface != LumenVectorGeometry.Coverage)
            .Select(q => q with { NativeSurface = null }).ToArray();
        var texture = new LumenSoftwareTexture(1, 1, new byte[] { 100, 120, 140, 255 });
        var image = LumenSoftwareRenderer.Render(new(150, 110, visible), [texture], 150, 110);
        int red(int x, int y) => image[(y * 150 + x) * 4];
        Assert.True(red(60, 16) > red(60, 40));
        Assert.True(red(16, 45) > red(60, 40));
        Assert.True(red(60, 84) < red(60, 40));
        Assert.True(red(124, 45) < red(60, 40));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(28)]
    [InlineData(260)]
    public void RoundedTabsHaveFiniteGeometryThroughoutTheirMorph(float width)
    {
        foreach (var peg in new[] { false, true })
        foreach (var quad in LumenVectorGeometry.OutlinedTab(new(20, 30, width, 24), LumenRenderColor.White, peg))
        foreach (var vertex in new[] { quad.TopLeft, quad.TopRight, quad.BottomLeft, quad.BottomRight })
        {
            Assert.True(float.IsFinite(vertex.X));
            Assert.True(float.IsFinite(vertex.Y));
            Assert.InRange(vertex.X, 15, 25 + width);
        }
    }

    [Fact]
    public void RevealedWavesKeepTheirPhaseWhileSkippingInvisibleGeometry()
    {
        var field = new LumenNativeSurfacePlacement(0, 0, 750, 450);
        var visible = new LumenNativeSurfacePlacement(140, 105, 165, 65);
        var full = LumenWavePattern.Draw(field, 1, 0).ToArray();
        var culled = LumenWavePattern.Draw(field, 1, 0, visible).ToArray();
        Assert.True(culled.Length < full.Length / 3);
        Assert.Equal(render(full), render(culled));

        byte[] render(IEnumerable<LumenRenderQuad> quads)
        {
            var drawing = quads.Select(q => LumenQuadGeometry.Clip(q, visible))
                .Where(q => q.HasValue).Select(q => q!.Value with { NativeSurface = null });
            return LumenSoftwareRenderer.Render(new(340, 200, drawing),
                [new LumenSoftwareTexture(1, 1, new byte[] { 255, 255, 255, 255 })], 340, 200);
        }
    }

    [Fact]
    public void WaveFieldPreservesTheEnclosingMaskAndOnlyUsesVectorSurfaces()
    {
        var quads = LumenWavePattern.Draw(new(40, 70, 510, 234), .7f, 2).ToArray();
        // Leave room in the shared transient buffer for the surrounding UI.
        Assert.InRange(quads.Length, 1, 10_000);
        Assert.All(quads, q =>
        {
            Assert.Equal(2, q.MaskDepth);
            Assert.Equal(LumenRenderMaskOperation.Draw, q.MaskOperation);
            Assert.True(q.NativeSurface == LumenVectorGeometry.White || q.NativeSurface == LumenWavePattern.Stroke);
            Assert.Equal(.7f, q.MultiplyColor.Alpha);
        });
    }
}
