using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Timing;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.Architecture.Tests;

public sealed class RenderSnapshotTests
{
    [Fact]
    public void ScriptedKeyboardPulsesApplyOnlyOnTheirExactTick()
    {
        var timeline = new SdlKeyboardTimeline([(2, SdlKeyboardKey.D), (2, SdlKeyboardKey.F)]);
        var live = new SdlKeyboardSnapshot([SdlKeyboardKey.Enter]);

        Assert.True(timeline.Apply(1, live).PressedKeys.SequenceEqual([SdlKeyboardKey.Enter]));
        Assert.True(timeline.Apply(2, live).PressedKeys.SequenceEqual(
            [SdlKeyboardKey.Enter, SdlKeyboardKey.D, SdlKeyboardKey.F]));
        Assert.True(timeline.Apply(3, live).PressedKeys.SequenceEqual([SdlKeyboardKey.Enter]));
    }

    [Fact]
    public void SdlKeyboardSnapshotMapsPhysicalDrumsToAuthoredPlayerControls()
    {
        var keyboard = new SdlKeyboardSnapshot([
            SdlKeyboardKey.D,
            SdlKeyboardKey.J,
            SdlKeyboardKey.V,
            SdlKeyboardKey.X,
            SdlKeyboardKey.Enter,
        ]);

        var lumen = LumenInputAdapter.CreateSnapshot(keyboard);

        Assert.True(lumen.IsDown('A'));
        Assert.True(lumen.IsDown('Z'));
        Assert.True(lumen.IsDown('F'));
        Assert.True(lumen.IsDown('C'));
        Assert.True(lumen.IsDown(13));
        Assert.False(lumen.IsDown('D'));
        Assert.False(lumen.IsDown('J'));
    }

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
        Assert.Equal(16d / 9d, frame.ContentAspectRatio);
    }

    [Fact]
    public void AdapterOmitsNativeSurfaceUntilItsTextureIsReady()
    {
        var pending = new LumenNativeSurfaceKey("pending-title");
        var ready = new LumenNativeSurfaceKey("ready-title");
        var snapshot = new LumenRenderSnapshot(
            1280,
            720,
            [
                quad(1, default) with { NativeSurface = pending },
                quad(2, default),
                quad(3, default) with { NativeSurface = ready },
            ]);

        var frame = LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.WaddamburoBlue,
            index => new RenderTextureId(index + 10),
            surface => surface == ready ? new RenderTextureId(99) : null);

        Assert.Equal([12u, 99u], frame.Quads.Select(quad => quad.Texture.Value));
    }

    [Theory]
    [InlineData(1000, 1000, 0, 218, 1000, 563)]
    [InlineData(2000, 720, 360, 0, 1280, 720)]
    [InlineData(2560, 1440, 0, 0, 2560, 1440)]
    public void LumenFrameAspectFitsItsLogicalStageToDrawablePixels(
        uint surfaceWidth,
        uint surfaceHeight,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var snapshot = new LumenRenderSnapshot(1280, 720, []);
        var frame = LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.WaddamburoBlue,
            _ => throw new InvalidOperationException());

        Assert.Equal(
            new RenderViewport(expectedX, expectedY, expectedWidth, expectedHeight),
            frame.ResolveViewport(surfaceWidth, surfaceHeight));
    }

    [Fact]
    public void FrameWithoutContentAspectUsesFullDrawableSurface()
    {
        var frame = new RenderFrame(RenderColor.WaddamburoBlue, []);

        Assert.Equal(new RenderViewport(0, 0, 1000, 700), frame.ResolveViewport(1000, 700));
    }

    [Fact]
    public void OffStageContentDoesNotChangeLogicalStageTransform()
    {
        var snapshot = new LumenRenderSnapshot(
            1280,
            720,
            [new LumenRenderQuad(
                0,
                new LumenRenderVertex(-128, -72, 0, 0),
                new LumenRenderVertex(1408, -72, 1, 0),
                new LumenRenderVertex(1408, 792, 1, 1),
                new LumenRenderVertex(-128, 792, 0, 1),
                LumenRenderColor.White,
                LumenRenderColor.Transparent)]);

        var frame = LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.WaddamburoBlue,
            _ => new RenderTextureId(1));

        var quad = Assert.Single(frame.Quads);
        Assert.Equal(-0.1f, quad.TopLeft.X, 5);
        Assert.Equal(-0.1f, quad.TopLeft.Y, 5);
        Assert.Equal(1.1f, quad.BottomRight.X, 5);
        Assert.Equal(1.1f, quad.BottomRight.Y, 5);
        Assert.Equal(16d / 9d, frame.ContentAspectRatio);
    }

    [Fact]
    public void LumenAdapterPreservesAddBlendCommands()
    {
        var additive = quad(1, default) with { Blend = LumenRenderBlend.Add };
        var snapshot = new LumenRenderSnapshot(1280, 720, [additive]);

        var frame = LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.WaddamburoBlue,
            _ => new RenderTextureId(1));

        Assert.Equal(RenderBlend.Add, Assert.Single(frame.Quads).Blend);
    }

    [Fact]
    public void FixedStepAccumulatorCarriesPartialTicks()
    {
        var clock = new FixedStepAccumulator();
        var ticks = 0;

        var first = clock.AddElapsed(TimeSpan.FromMilliseconds(10), () => ticks++);
        var second = clock.AddElapsed(TimeSpan.FromMilliseconds(10), () => ticks++);

        Assert.Equal(0, first.ExecutedTicks);
        Assert.Equal(1, second.ExecutedTicks);
        Assert.Equal(1, ticks);
        Assert.Equal(0.2, clock.InterpolationFraction, 10);
    }

    [Fact]
    public void FixedStepAccumulatorCapsCatchUpAndReportsDroppedTicks()
    {
        var clock = new FixedStepAccumulator(ticksPerSecond: 60, maximumCatchUpTicks: 5);
        var ticks = 0;

        var update = clock.AddElapsed(TimeSpan.FromSeconds(1), () => ticks++);

        Assert.Equal(new FixedStepUpdate(5, 55), update);
        Assert.Equal(5, ticks);
        Assert.Equal(0, clock.InterpolationFraction, 10);
    }

    [Fact]
    public void FixedStepAccumulatorStopsAtExactRequestedTickWithoutDroppingRemainder()
    {
        var clock = new FixedStepAccumulator();
        var ticks = 0;

        var update = clock.AddElapsed(
            TimeSpan.FromSeconds(1),
            () => ticks++,
            remainingTickLimit: 3);

        Assert.Equal(new FixedStepUpdate(3, 0), update);
        Assert.Equal(3, ticks);
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
