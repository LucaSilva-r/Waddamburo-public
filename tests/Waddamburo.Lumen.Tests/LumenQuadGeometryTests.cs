using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenQuadGeometryTests
{
    // Portrait texture rotated clockwise by its display matrix.
    private static LumenRenderQuad text() => new(3,
        new(100, 20, 0, 1), new(700, 20, 0, 0), new(700, 116, 1, 0), new(100, 116, 1, 1),
        LumenRenderColor.White, LumenRenderColor.Transparent, NativeSurface: new("title"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void HorizontalMorphReachesZeroAndClipsAllRemainingFringe(float width)
    {
        var source = text(); var bounds = LumenNativeFrame.Bounds(source);
        var target = bounds with { X = 400, Width = width };
        var fringe = LumenQuadGeometry.Move(source, 6, 6);
        foreach (var quad in new[] { source, fringe })
        {
            var result = LumenPanelMorph.WarpHorizontal(quad, bounds, target);
            if (width == 0) { Assert.Null(result); continue; }
            Assert.NotNull(result);
            foreach (var vertex in new[] { result.Value.TopLeft, result.Value.TopRight, result.Value.BottomLeft, result.Value.BottomRight })
            {
                Assert.True(float.IsFinite(vertex.X)); Assert.True(float.IsFinite(vertex.U));
                if (width < 8) Assert.InRange(vertex.X, target.X, target.X + target.Width);
            }
        }
    }

    [Theory]
    [InlineData(.8f, .8f)]
    [InlineData(.8f, .13333f)]
    public void CategorySkinsShareTheGoldApertureDespiteDifferentOpaqueFringes(float topFringe, float bottomFringe)
    {
        var raw = new LumenNativeSurfacePlacement(376.402f, 171, 528, 234);
        var visible = raw with { X = raw.X + .8f, Y = raw.Y + topFringe, Width = raw.Width - .8f, Height = raw.Height - topFringe + bottomFringe };
        var fitted = VerticalCategoryFrame.FitPreview(raw, visible);
        Assert.Equal(376.5f, fitted.X, 3); Assert.Equal(171.4f, fitted.Y, 3);
        Assert.Equal(525.9f, fitted.Width, 3); Assert.Equal(233.2f, fitted.Height, 3);
        var moved = VerticalCategoryFrame.FitPreview(raw with { Y = raw.Y + .25f }, visible with { Y = visible.Y + .25f });
        Assert.Equal(fitted.Y + .25f, moved.Y, 3);
        Assert.Equal(visible, VerticalCategoryFrame.FitPreview(raw with { Height = 58.35f }, visible));
    }

    [Fact]
    public void BoundsIncludesEveryShearedCornerAndEveryPieceWithFractionalCoordinates()
    {
        var first = text() with { TopLeft = new(-3.25f, 7, 0, 0), TopRight = new(12, -5.5f, 0, 0),
            BottomLeft = new(17.5f, 30.25f, 0, 0), BottomRight = new(-8.125f, 21, 0, 0) };
        var second = LumenQuadGeometry.Move(first, 100.125f, 10.5f);
        var expected = new LumenNativeSurfacePlacement(-8.125f, -5.5f, 125.75f, 46.25f);
        Assert.Equal(new(-8.125f, -5.5f, 25.625f, 35.75f), LumenNativeFrame.Bounds(first));
        Assert.Equal(expected, LumenNativeFrame.Bounds(new[] { first, second }));
        Assert.Equal(expected, LumenNativeFrame.Bounds(new[] { first, second }.Where(_ => true)));
        Assert.Throws<InvalidOperationException>(() => LumenNativeFrame.Bounds(Array.Empty<LumenRenderQuad>()));
    }

    [Fact]
    public void SubtitleKeepsItsRasterSizeAndFollowsFractionalParentMovement()
    {
        var source = text();
        var layout = new LumenTextLayout(600, 100, [new(300, 2, 64), new(200, 66, 32)]);
        var subtitle = LumenTextMarquee.Compose(source, layout, 200, 800, 0).Last();
        var box = LumenNativeFrame.Bounds([subtitle]);
        Assert.Equal(MathF.Round(box.Height), box.Height);
        var originalHeight = LumenNativeFrame.Bounds([source]).Height * 32 / 100;
        Assert.Equal(206f / originalHeight, box.Width / box.Height, 3);
        Assert.InRange(Math.Abs(box.Height - originalHeight), 0, .5f);
        foreach (var delta in new[] { .125f, .25f, .5f, .75f })
        {
            var moved = LumenTextMarquee.Compose(LumenQuadGeometry.Move(source, 0, delta), layout, 200, 800, 0).Last();
            var movedBox = LumenNativeFrame.Bounds([moved]);
            Assert.Equal(box.Y + delta, movedBox.Y, 4);
            Assert.Equal(box.Height, movedBox.Height, 4);
        }
    }

    [Fact]
    public void ClippingRotatedPortraitTextPreservesItsUvAndSurface()
    {
        var clipped = LumenQuadGeometry.Clip(text(), new(250, 44, 300, 48))!.Value;
        Assert.Equal(new LumenRenderVertex(250, 44, .25f, .75f), clipped.TopLeft);
        Assert.Equal(new LumenRenderVertex(550, 92, .75f, .25f), clipped.BottomRight);
        Assert.Equal(text().NativeSurface, clipped.NativeSurface);
        Assert.Null(LumenQuadGeometry.Clip(text(), new(800, 0, 10, 10)));
    }

    [Fact]
    public void LongTitleLoopsWithinTheCardWhileItsShortSubtitleStaysCentred()
    {
        var layout = new LumenTextLayout(600, 96, [new(590, 2, 64), new(100, 66, 28)]);
        var initial = LumenTextMarquee.Compose(text(), layout, 200, 600, 0).ToArray();
        var moved = LumenTextMarquee.Compose(text(), layout, 200, 600, 100).ToArray();
        Assert.True(moved[0].TopLeft.V < initial[0].TopLeft.V);
        Assert.Equal(initial[^1], moved[^1]);
        for (var tick = 0; tick < 2200; tick += 7)
        {
            var quads = LumenTextMarquee.Compose(text(), layout, 200, 600, tick).ToArray();
            Assert.All(quads, quad =>
            {
                Assert.InRange(quad.TopLeft.X, 200, 600);
                Assert.InRange(quad.BottomRight.X, 200, 600);
            });
            Assert.Contains(quads, quad => quad.TopLeft.Y < 85);
        }
    }

    [Theory]
    [InlineData(.84f, 100)]
    [InlineData(.9f, 950)]
    [InlineData(.97f, 1725)]
    public void MarqueePhaseAndSubtitleStayAttachedWhenTheCardChangesPerspective(float scale, float ticks)
    {
        var layout = new LumenTextLayout(600, 96, [new(590, 2, 64), new(480, 66, 28)]);
        LumenRenderVertex project(LumenRenderVertex v) => v with { X = 75 + v.X * scale, Y = 30 + v.Y * scale };
        LumenRenderQuad projectQuad(LumenRenderQuad q) => q with { TopLeft = project(q.TopLeft), TopRight = project(q.TopRight),
            BottomLeft = project(q.BottomLeft), BottomRight = project(q.BottomRight) };
        var expected = LumenTextMarquee.Compose(text(), layout, 200, 600, ticks).Select(projectQuad).ToArray();
        var actual = LumenTextMarquee.Compose(projectQuad(text()), layout, 75 + 200 * scale, 75 + 600 * scale,
            ticks, scale: scale).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var a = expected[index]; var b = actual[index];
            foreach (var (first, second) in new[] { (a.TopLeft, b.TopLeft), (a.TopRight, b.TopRight),
                         (a.BottomLeft, b.BottomLeft), (a.BottomRight, b.BottomRight) })
            {
                Assert.InRange(Math.Abs(first.X - second.X), 0, .001f);
                Assert.InRange(Math.Abs(first.Y - second.Y), 0, .001f);
                Assert.InRange(Math.Abs(first.U - second.U), 0, .0001f);
                Assert.InRange(Math.Abs(first.V - second.V), 0, .0001f);
            }
        }
    }

    [Fact]
    public void TwoLongLinesUseIndependentPeriodsAndReturnToTheExactSamePose()
    {
        var layout = new LumenTextLayout(600, 96, [new(590, 2, 64), new(480, 66, 28)]);
        var a = LumenTextMarquee.Compose(text(), layout, 200, 600, 0).ToArray();
        // Title is 596 pixels wide, followed by a 48-pixel gap, at .75 pixels/tick.
        var b = LumenTextMarquee.Compose(text(), layout, 200, 600, 60 + (596 + 48) / .75f).ToArray();
        Assert.Equal(a[0], b[0]);
        Assert.NotEqual(a[^1], b[^1]);
    }

    [Fact]
    public void EveryCyclePausesForOneSecondAtTheBeginningOfTheTitle()
    {
        var layout = new LumenTextLayout(600, 96, [new(590, 0, 64)]);
        var initial = LumenTextMarquee.Compose(text(), layout, 200, 600, 0).ToArray();
        Assert.Equal(initial, LumenTextMarquee.Compose(text(), layout, 200, 600, 60));
        Assert.NotEqual(initial[0], LumenTextMarquee.Compose(text(), layout, 200, 600, 61).First());
        var period = 60 + (596 + 48) / .75f;
        Assert.Equal(initial, LumenTextMarquee.Compose(text(), layout, 200, 600, period));
        Assert.Equal(initial, LumenTextMarquee.Compose(text(), layout, 200, 600, period + 59));
    }

    [Fact]
    public void NextDifficultyFadesOnlyInTheHintStripAndNeverDrawsBeyondIt()
    {
        var quads = LumenQuadGeometry.FadeRight(text(), 650, 680).ToArray();
        Assert.Equal(1, quads[0].MultiplyColor.Alpha);
        Assert.True(quads[1].MultiplyColor.Alpha > quads[^1].MultiplyColor.Alpha);
        Assert.All(quads, q => Assert.True(q.BottomRight.X <= 680));
        Assert.InRange(quads[^1].MultiplyColor.Alpha, 0, .04f);
    }

    [Fact]
    public void FadingAClippedCardKeepsItsStencilWritesOpaque()
    {
        var mask = text() with { MaskOperation = LumenRenderMaskOperation.Push };
        Assert.All(LumenQuadGeometry.FadeRight(mask, 650, 680),
            q => Assert.Equal(1, q.MultiplyColor.Alpha));
    }

    [Fact]
    public void PatternRotatesClockwiseAndRepeatsWithoutChangingItsTileSize()
    {
        var source = text() with { TopLeft = new(100, 20, 0, 0), TopRight = new(700, 20, 1, 0),
            BottomLeft = new(100, 116, 0, 1), BottomRight = new(700, 116, 1, 1) };
        var tiles = LumenQuadGeometry.TileClockwise(source, 120, 80).ToArray();
        Assert.Equal(10, tiles.Length);
        Assert.Equal(new LumenRenderVertex(100, 20, 0, 1), tiles[0].TopLeft);
        Assert.Equal(new LumenRenderVertex(220, 20, 0, 0), tiles[0].TopRight);
        Assert.Equal(120, tiles[1].TopRight.X - tiles[1].TopLeft.X);
        Assert.Equal(80, tiles[0].BottomLeft.Y - tiles[0].TopLeft.Y);
        Assert.Equal(116, tiles[^1].BottomRight.Y);
        Assert.Equal(700, tiles[^1].BottomRight.X);
        var rotated = LumenQuadGeometry.TileClockwise(text(), 120, 80).First();
        Assert.Equal(1, rotated.TopLeft.U);
        Assert.Equal(1, rotated.TopLeft.V);
        Assert.Equal(80, rotated.TopRight.X - rotated.TopLeft.X);
    }

    [Theory]
    [InlineData(59)]
    [InlineData(246)]
    [InlineData(700)]
    public void NativeBevelAlwaysLeavesTheSameFourPixelRim(float height)
    {
        var source = text() with { NativeSurface = null,
            BottomLeft = new(100, 20 + height, 1, 1), BottomRight = new(700, 20 + height, 1, 0) };
        var frame = LumenNativeFrame.Compose([source]).ToArray();
        Assert.Equal(96, frame[0].TopLeft.X);
        Assert.Equal(704, frame[0].TopRight.X);
        Assert.Equal(24 + height, frame[0].BottomLeft.Y);
        Assert.Equal(0, frame[0].MultiplyColor.Red);
        var inside = LumenNativeFrame.Bounds(frame.Skip(1));
        Assert.Equal(100, inside.X);
        Assert.Equal(20, inside.Y);
        Assert.Equal(600, inside.Width);
        Assert.Equal(height, inside.Height);
    }

    [Fact]
    public void NativeFrameMovesContinuouslyAndKeepsEqualRimsBetweenSimulationTicks()
    {
        var source = text() with { NativeSurface = null };
        var initial = LumenNativeFrame.Compose([source]).ToArray();
        foreach (var fraction in new[] { .125f, .25f, .5f, .75f, 1 })
        {
            var frame = LumenNativeFrame.Compose([LumenQuadGeometry.Move(source, fraction, fraction)]).ToArray();
            Assert.Equal(initial[0].TopLeft.X + fraction, frame[0].TopLeft.X, 4);
            Assert.Equal(initial[0].TopLeft.Y + fraction, frame[0].TopLeft.Y, 4);
            var rim = LumenNativeFrame.Bounds([frame[0]]);
            var inside = LumenNativeFrame.Bounds(frame.Skip(1));
            Assert.Equal(4, inside.X - rim.X, 4);
            Assert.Equal(4, inside.Y - rim.Y, 4);
            Assert.Equal(4, rim.X + rim.Width - inside.X - inside.Width, 4);
            Assert.Equal(4, rim.Y + rim.Height - inside.Y - inside.Height, 4);
        }
    }

    [Fact]
    public void LongTitleAdvancesAtFractionalRenderTimesWithoutMovingItsSubtitle()
    {
        var layout = new LumenTextLayout(600, 96, [new(590, 2, 64), new(100, 66, 28)]);
        var initial = LumenTextMarquee.Compose(text(), layout, 200, 600, 100).ToArray();
        foreach (var fraction in new[] { .125f, .25f, .5f, .75f })
        {
            var frame = LumenTextMarquee.Compose(text(), layout, 200, 600, 100 + fraction).ToArray();
            Assert.Equal(initial[^1], frame[^1]);
            // The portrait source's V axis runs in the opposite screen-X direction.
            Assert.Equal(initial[0].TopLeft.V - .75f * fraction / 600, frame[0].TopLeft.V, 5);
        }
    }
    [Fact]
    public void PreviousDifficultyFadesIntoTheLeftEdgeWithoutChangingItsUvs()
    {
        var quads = LumenQuadGeometry.FadeLeft(text(), 120, 150).ToArray();
        Assert.All(quads, q => Assert.True(q.TopLeft.X >= 120));
        var strips = quads.OrderBy(q => q.TopLeft.X).ToArray();
        Assert.InRange(strips[0].MultiplyColor.Alpha, 0, .04f);
        Assert.Equal(1, strips[^1].MultiplyColor.Alpha);
        foreach (var quad in quads)
        {
            Assert.Equal(1 - (quad.TopLeft.X - 100) / 600, quad.TopLeft.V, 5);
            Assert.Equal(1 - (quad.TopRight.X - 100) / 600, quad.TopRight.V, 5);
        }
    }

    [Fact]
    public void FadingBothEdgesKeepsStencilWritesOpaque()
    {
        var mask = text() with { MaskOperation = LumenRenderMaskOperation.Push };
        var quads = LumenQuadGeometry.FadeLeft(mask, 120, 150)
            .SelectMany(q => LumenQuadGeometry.FadeRight(q, 650, 680));
        Assert.All(quads, q => Assert.Equal(1, q.MultiplyColor.Alpha));
    }

}
