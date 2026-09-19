using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Architecture.Tests;

public sealed class RenderSnapshotTests
{
    [Fact]
    public void AdapterNormalizesStageCoordinatesAndPreservesDrawOrder()
    {
        var snapshot = new LumenRenderSnapshot(
            1280,
            720,
            [
                quad(7, new LumenRenderVertex(320, 180, 0.1f, 0.2f)),
                quad(3, new LumenRenderVertex(640, 360, 0.3f, 0.4f)),
            ]);

        var frame = LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.WaddamburoBlue,
            textureIndex => new RenderTextureId(textureIndex + 100));

        Assert.Equal(RenderColor.WaddamburoBlue, frame.ClearColor);
        Assert.Equal([107u, 103u], frame.Quads.Select(command => command.Texture.Value));
        Assert.Equal(new RenderVertex(0.25f, 0.25f, 0.1f, 0.2f), frame.Quads[0].TopLeft);
        Assert.Equal(new RenderVertex(0.5f, 0.5f, 0.3f, 0.4f), frame.Quads[1].TopLeft);
        Assert.Equal(RenderSampling.Nearest, frame.Quads[0].Sampling);
    }

    [Fact]
    public void SnapshotCopiesItsCommandSequence()
    {
        var source = new List<LumenRenderQuad> { quad(1, default) };
        var snapshot = new LumenRenderSnapshot(1, 1, source);

        source.Add(quad(2, default));

        Assert.Single(snapshot.Quads);
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, float.NaN)]
    [InlineData(float.PositiveInfinity, 720)]
    public void SnapshotRejectsInvalidStageDimensions(float width, float height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumenRenderSnapshot(width, height, []));
    }

    private static LumenRenderQuad quad(uint textureIndex, LumenRenderVertex topLeft) =>
        new(
            textureIndex,
            topLeft,
            default,
            default,
            default,
            LumenRenderColor.White,
            LumenRenderColor.Transparent,
            UseNearestSampling: true);
}
