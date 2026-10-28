using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Architecture.Tests;

public sealed class FramePixelGridTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void OppositeBordersHaveTheSamePixelWidthAtFractionalPositionsAndDuringMorph(int width, int height)
    {
        foreach (var phase in new[] { .17f, .4f, .5f, .73f })
        foreach (var size in new[] { 40.25f, 200.7f, 525.9f })
        {
            var source = new LumenNativeSurfacePlacement(376.5f, 171.4f, 525.9f, 233.2f);
            var target = source with { X = 639 - size / 2 + phase, Y = source.Y + phase, Width = size };
            var quads = LumenVectorGeometry.Frame(source, new("vector-ui:paint:test"))
                .Select(q => LumenPanelMorph.WarpHorizontal(q, source, target)).OfType<LumenRenderQuad>();
            var frame = compose(quads);
            var viewport = frame.ResolveViewport((uint)width, (uint)height);
            var raster = frame.Quads.Select(q => RenderPixelGrid.Snap(q, viewport.Width, viewport.Height)).ToArray();
            var black = raster.Single(q => q.Texture.Value == 1 && q.MultiplyColor.Red == 0 && q.MultiplyColor.Alpha == 1);
            var body = raster.First(q => q.Texture.Value == 2);
            var borders = new[] {
                (body.TopLeft.X - black.TopLeft.X) * viewport.Width,
                (black.TopRight.X - body.TopRight.X) * viewport.Width,
                (body.TopLeft.Y - black.TopLeft.Y) * viewport.Height,
                (black.BottomLeft.Y - body.BottomLeft.Y) * viewport.Height,
            };
            var expected = MathF.Round(4 * viewport.Width / 1280f, MidpointRounding.AwayFromZero);
            Assert.All(borders, border => Assert.InRange(border, expected - .001f, expected + .001f));
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public void PegOutlineAndAntialiasNeverExtendBelowTheTopBorder(int width, int height)
    {
        var card = new LumenNativeSurfacePlacement(389.4f, 487.6f, 504, 58.35f);
        var peg = LumenVectorGeometry.FolderPeg(new(card.X, card.Y - 10, 35.2f, 16), new(1, .3f, .5f, 1), card);
        var frame = compose(peg);
        var viewport = frame.ResolveViewport((uint)width, (uint)height);
        var join = MathF.Floor(card.Y / 720 * viewport.Height + .5005f)
            + MathF.Floor(4 / 720f * viewport.Height + .5005f);
        var raster = frame.Quads.Select(q => RenderPixelGrid.Snap(q, viewport.Width, viewport.Height));
        Assert.All(raster, q =>
        {
            Assert.NotNull(q.PixelFrame);
            foreach (var vertex in new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight })
                Assert.True(vertex.Y * viewport.Height <= join + .001f);
        });
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void CategoryLabelClosesTheTopRimAtTheSamePixelAsItsBevel(int width, int height)
    {
        foreach (var fraction in new[] { .1f, .4f, .75f })
        {
            var card = new LumenNativeSurfacePlacement(376.5f, 171.4f + fraction, 525.9f, 233.2f);
            // Native label bounds end before the bevel; the gap becomes visible
            // at high DPI unless both use a shared, open-bottom pixel join.
            var label = new LumenNativeSurfacePlacement(395.25f, card.Y - 48, 260, 51);
            var output = compose(LumenVectorGeometry.CategoryTab(label, LumenRenderColor.White, card));
            var viewport = output.ResolveViewport((uint)width, (uint)height);
            var join = MathF.Floor(card.Y / 720 * viewport.Height + .5005f)
                + MathF.Floor(4 / 720f * viewport.Height + .5005f);
            var raster = output.Quads.Select(q => RenderPixelGrid.Snap(q, viewport.Width, viewport.Height)).ToArray();
            Assert.All(raster, q =>
            {
                Assert.NotNull(q.PixelFrame);
                foreach (var vertex in new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight })
                    Assert.True(vertex.Y * viewport.Height <= join + .001f);
            });
            Assert.Contains(raster, q => q.Texture.Value == 1 && q.MultiplyColor.Red == 1
                && new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight }
                    .Count(v => Math.Abs(v.Y * viewport.Height - join) < .001f) >= 2);
        }
    }

    [Theory]
    [InlineData(1280, 720, .94f)]
    [InlineData(1920, 1080, .94f)]
    [InlineData(2560, 1440, .84f)]
    public void PerspectiveTabsAndPegsKeepTheirJoinOnTheScaledFrameBorder(int width, int height, float scale)
    {
        var card = new LumenNativeSurfacePlacement(389.4f, 487.6f, 504, 58.35f);
        foreach (var peg in new[] { false, true })
        {
            var tab = peg ? LumenVectorGeometry.FolderPeg(new(card.X, card.Y - 10, 35.2f, 16), LumenRenderColor.White, card)
                : LumenVectorGeometry.CategoryTab(new(card.X + 20, card.Y - 48, 260, 51), LumenRenderColor.White, card);
            LumenRenderVertex project(LumenRenderVertex v) => v with { X = v.X * scale + 37.4f, Y = v.Y * scale + 21.7f };
            var output = compose(tab.Select(q => q with { TopLeft = project(q.TopLeft), TopRight = project(q.TopRight),
                BottomLeft = project(q.BottomLeft), BottomRight = project(q.BottomRight), FrameBorderScale = scale }));
            var viewport = output.ResolveViewport((uint)width, (uint)height);
            var join = MathF.Floor((card.Y * scale + 21.7f) / 720 * viewport.Height + .5005f)
                + MathF.Floor(4 * scale / 720 * viewport.Height + .5005f);
            var raster = output.Quads.Select(q => RenderPixelGrid.Snap(q, viewport.Width, viewport.Height)).ToArray();
            Assert.All(raster, q =>
            {
                Assert.NotNull(q.PixelFrame);
                foreach (var v in new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight })
                    Assert.True(v.Y * viewport.Height <= join + .001f);
            });
            Assert.Contains(raster, q => q.Texture.Value == 1 && q.MultiplyColor.Red == 1
                && new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight }
                    .Count(v => Math.Abs(v.Y * viewport.Height - join) < .001f) >= 2);
        }
    }

    [Fact]
    public void FrameSnappingDoesNotChangeTheCategoryLabelOrOtherArtwork()
    {
        var label = LumenVectorGeometry.OutlinedTab(new(400.25f, 120.4f, 260, 52), LumenRenderColor.White).ToArray();
        var frame = LumenVectorGeometry.Frame(new(376.5f, 171.4f, 525.9f, 233.2f), new("vector-ui:paint:test"));
        var output = compose(frame.Concat(label));
        var caption = output.Quads.TakeLast(label.Length).ToArray();
        Assert.All(caption, q => Assert.Null(q.PixelFrame));
        for (var i = 0; i < label.Length; i++)
        {
            Assert.InRange(Math.Abs(label[i].TopLeft.X / 1280 - caption[i].TopLeft.X), 0, .0000001f);
            Assert.InRange(Math.Abs(label[i].TopLeft.Y / 720 - caption[i].TopLeft.Y), 0, .0000001f);
        }
    }

    private static RenderFrame compose(IEnumerable<LumenRenderQuad> quads) => LumenRenderFrameAdapter.Compose(
        new(1280, 720, quads), RenderColor.Transparent, _ => new(99),
        key => new RenderTextureId(key == LumenVectorGeometry.White ? 1U
            : key.Value == "vector-ui:paint:test" ? 2U : 3U));
}
