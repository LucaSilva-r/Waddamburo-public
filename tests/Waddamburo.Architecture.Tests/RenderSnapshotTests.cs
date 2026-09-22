using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Timing;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.Architecture.Tests;

public sealed class RenderSnapshotTests
{
    [Fact]
    public void AdapterPreservesMaskOrderDepthAndTextureCoordinates()
    {
        var shape = new LumenRenderQuad(3, new(0, 0, 0, 0), new(100, 0, 1, 0),
            new(100, 100, 1, 1), new(0, 100, 0, 1), LumenRenderColor.White, LumenRenderColor.Transparent);
        var snapshot = new LumenRenderSnapshot(200, 100, [
            shape with { MaskOperation = LumenRenderMaskOperation.Push },
            shape with { MaskDepth = 1 },
            shape with { MaskOperation = LumenRenderMaskOperation.Pop, MaskDepth = 1 },
            shape,
        ]);
        var frame = LumenRenderFrameAdapter.Compose(snapshot, RenderColor.White, index => new(index + 10));
        Assert.Equal([RenderMaskOperation.Push, RenderMaskOperation.Draw, RenderMaskOperation.Pop,
            RenderMaskOperation.Draw], frame.Quads.Select(q => q.MaskOperation));
        Assert.Equal(new byte[] { 0, 1, 1, 0 }, frame.Quads.Select(q => q.MaskDepth));
        Assert.All(frame.Quads, q =>
        {
            Assert.Equal(new RenderTextureId(13), q.Texture);
            Assert.Equal(new RenderVertex(0.5f, 1, 1, 1), q.BottomRight);
        });
    }

    [Fact]
    public void ScriptedInputPreservesReleasedLivePressesAndTheirTimestamps()
    {
        var early = TimeSpan.FromMilliseconds(10);
        var later = TimeSpan.FromMilliseconds(12);
        var now = TimeSpan.FromMilliseconds(20);
        var live = new SdlKeyboardSnapshot([], [new(SdlKeyboardKey.J, later), new(SdlKeyboardKey.F, early)], now);
        var timeline = new SdlKeyboardTimeline([(2, SdlKeyboardKey.D)]);
        var merged = timeline.Apply(2, live);
        Assert.False(merged.IsDown(SdlKeyboardKey.F));
        Assert.Equal(new[] { SdlKeyboardKey.F, SdlKeyboardKey.J, SdlKeyboardKey.D }, merged.Presses.Select(press => press.Key));
        Assert.Equal(new[] { early, later, now }, merged.Presses.Select(press => press.Timestamp));
        Assert.Equal(now, merged.Timestamp);
    }

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

    [Theory]
    [InlineData(SdlKeyboardKey.D, 'A')]
    [InlineData(SdlKeyboardKey.F, 'Z')]
    [InlineData(SdlKeyboardKey.J, 'Z')]
    [InlineData(SdlKeyboardKey.K, 'S')]
    [InlineData(SdlKeyboardKey.Escape, (char)27)]
    public void PresentationOnlyInputDoesNotForwardKeysOrConsumeNativePresses(
        SdlKeyboardKey key, char authoredKey)
    {
        var time = TimeSpan.FromSeconds(1);
        var press = new SdlKeyPress(key, time);
        var keyboard = new SdlKeyboardSnapshot([key], [press], time);

        var presentation = LumenInputAdapter.CreateSnapshot(keyboard, LumenInputMode.PresentationOnly);

        Assert.Same(LumenInputSnapshot.Empty, presentation);
        Assert.False(presentation.IsDown(authoredKey));
        Assert.True(keyboard.IsDown(key));
        Assert.Equal(press, Assert.Single(keyboard.Presses));
        Assert.Equal(time, keyboard.Timestamp);
        // Reusing the same state for a menu still delivers the authored mapping.
        Assert.True(LumenInputAdapter.CreateSnapshot(keyboard).IsDown(authoredKey));
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
