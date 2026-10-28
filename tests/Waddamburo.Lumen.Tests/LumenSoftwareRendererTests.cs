using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenSoftwareRendererTests
{
    [Fact]
    public void SharedTriangleEdgeIsPaintedOnceAndTransparentOutputUsesStraightAlpha()
    {
        var texture = new LumenSoftwareTexture(1, 1, new byte[] { 255, 0, 0, 128 });
        var image = LumenSoftwareRenderer.Render(new LumenRenderSnapshot(4, 4, [quad(4)]), [texture], 4, 4);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.Equal(255, image[pixel * 4]); Assert.Equal(128, image[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void MaskPushAndPopClipOnlyTheEnclosedDraw()
    {
        var texture = new LumenSoftwareTexture(1, 1, new byte[] { 255, 255, 255, 255 });
        var push = quad(2) with { MaskOperation = LumenRenderMaskOperation.Push };
        var paint = quad(4) with { MaskDepth = 1 };
        var pop = push with { MaskOperation = LumenRenderMaskOperation.Pop, MaskDepth = 1 };
        var image = LumenSoftwareRenderer.Render(new LumenRenderSnapshot(4, 4, [push, paint, pop]), [texture], 4, 4);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++) Assert.Equal(x < 2 && y < 2 ? 255 : 0, image[(y * 4 + x) * 4 + 3]);
    }

    [Theory]
    [InlineData(LumenRenderBlend.Normal, 128)]
    [InlineData(LumenRenderBlend.Multiply, 64)]
    [InlineData(LumenRenderBlend.Screen, 192)]
    [InlineData(LumenRenderBlend.Add, 255)]
    public void BlendModesUseTheDestination(LumenRenderBlend blend, int expected)
    {
        var texture = new LumenSoftwareTexture(1, 1, new byte[] { 128, 128, 128, 255 });
        var image = LumenSoftwareRenderer.Render(new LumenRenderSnapshot(1, 1, [quad(1), quad(1) with { Blend = blend }]), [texture], 1, 1);
        Assert.InRange(image[0], expected - 1, expected + 1);
    }

    [Fact]
    public void MissingHostSurfaceIsNotMisrepresentedAsAnEmbeddedTexture()
    {
        var texture = new LumenSoftwareTexture(1, 1, new byte[] { 255, 0, 0, 255 });
        var image = LumenSoftwareRenderer.Render(new LumenRenderSnapshot(1, 1, [quad(1) with { NativeSurface = new LumenNativeSurfaceKey("host") }]), [texture], 1, 1);
        Assert.Equal(new byte[4], image);
    }

    private static LumenRenderQuad quad(float size) => new(0, new(0, 0, 0, 0), new(size, 0, 1, 0),
        new(size, size, 1, 1), new(0, size, 0, 1), LumenRenderColor.White, LumenRenderColor.Transparent);
}
