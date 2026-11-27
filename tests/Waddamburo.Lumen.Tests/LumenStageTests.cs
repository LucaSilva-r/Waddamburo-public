using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Package;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenStageTests
{
    // On a 21:9 stage (1680) the 1280 box sits 200 in from each side: a right-anchored layer keeps its distance to the
    // right edge, a stretched one spans the stage, a centred one stays; and the end mapping puts 1280-stage points there.
    [Fact]
    public void AnchorsHoldTheirEdge()
    {
        var before = LumenStage.Width;
        try
        {
            LumenStage.Width = LumenStage.Fit(21d / 9);
            Assert.Equal(1680, LumenStage.Width);
            var at = new LumenMatrix(1, 0, 0, 1, 1200, 10);
            Assert.Equal(1400, LumenStage.Anchored(at, LumenAnchor.Right).X);
            Assert.Equal(1000, LumenStage.Anchored(at, LumenAnchor.Left).X);
            Assert.Equal(1200, LumenStage.Anchored(at, LumenAnchor.Center).X);
            var stretched = LumenStage.Anchored(LumenMatrix.Identity, LumenAnchor.Stretch);
            Assert.Equal((-200f, 1480f), (stretched.Transform(0, 0).X, stretched.Transform(1280, 0).X));
            // Through the end mapping: the stretched edges are the stage's, a right-anchored 1280 is the right edge.
            Assert.Equal((0f, 1f), (LumenStage.ToStage(-200), LumenStage.ToStage(1480)));
            Assert.Equal(640, LumenStage.FromStage(.5f));
            // A touch button at the left (made at 0.05) is drawn 200 further left; a touch there finds it again.
            Assert.Equal(.05f, LumenStage.FromSides(.05f + LumenStage.SideShift(.05f)), 4);
            Assert.Equal(.5f, LumenStage.FromSides(.5f)); // the drum, in the middle, stays
            LumenStage.Width = LumenStage.Fit(4d / 3);
            Assert.Equal((960f, 1040f), (LumenStage.Width, LumenStage.Anchored(at, LumenAnchor.Right).X)); // the right edge at 1120: 80 in
            Assert.Equal(2560, LumenStage.Fit(32d / 9)); // any wider screen is filled
            Assert.Equal(LumenStage.MinWidth, LumenStage.Fit(9d / 16)); // a portrait one letterboxed
        }
        finally
        {
            LumenStage.Width = before;
        }
    }

    // A package's clip anchored right moves with the right edge (with what it holds); a nested one would add its own.
    [Fact]
    public void AnchoredClipsMoveWithTheEdge()
    {
        const string movie = """
            { "format": 1, "root": "main",
              "texts": { "box": { "rect": [0, 0, 10, 10], "text": "x" } },
              "sprites": {
                "clip": { "frames": 1, "layers": [ { "character": "box", "keys": [ { "frame": 0, "x": 5 } ] } ] },
                "main": { "frames": 1, "layers": [
                  { "character": "clip", "name": "edge", "anchor": "right", "keys": [ { "frame": 0, "x": 1200, "y": 10 } ] },
                  { "character": "box", "name": "middle", "keys": [ { "frame": 0, "x": 600 } ] } ] } } }
            """;
        var assembled = MoviePackage.Assemble(movie, _ => "");
        var read = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb), validationContext: new LmbSemanticValidationContext(0));
        var anchors = assembled.Anchors.ToDictionary(static each => (each.Sprite, (long)each.Depth), static each => LumenStage.Parse(each.Anchor));
        var player = new LumenPlayer(read.Value, 1280, 720) { ClipAnchors = anchors };
        var before = LumenStage.Width;
        try
        {
            LumenStage.Width = 1680;
            var xs = player.CreateRenderSnapshot().Quads.Select(static quad => quad.TopLeft.X).Order().ToArray();
            Assert.Equal([600f, 1405f], xs);
        }
        finally
        {
            LumenStage.Width = before;
        }
        Assert.Throws<FormatException>(() => MoviePackage.Assemble(movie.Replace("\"right\"", "\"top\""), _ => ""));
    }

    private static LumenPlayer package(string layers, string shapes)
    {
        var movie = $$"""
            { "format": 1, "root": "main", "textures": { "t": "t.png" },
              "shapes": { {{shapes}} },
              "sprites": { "main": { "frames": 1, "layers": [ {{layers}} ] } } }
            """;
        var assembled = MoviePackage.Assemble(movie, _ => "");
        var read = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb), validationContext: new LmbSemanticValidationContext(1));
        return new LumenPlayer(read.Value, 1280, 720)
        {
            ClipAnchors = assembled.Anchors.ToDictionary(static each => (each.Sprite, (long)each.Depth), static each => LumenStage.Parse(each.Anchor)),
            ShapeSlices = assembled.Slices.ToDictionary(static each => each.Shape, static each => (each.Left, each.Top, each.Right, each.Bottom)),
        };
    }

    private static float[] edges(LumenPlayer player) =>
        [.. player.CreateRenderSnapshot().Quads.SelectMany(static quad => (float[])[quad.TopLeft.X, quad.TopRight.X]).Distinct().Order()];

    // On a 21:9 stage (200 more each side): a stretched clip keeps each side's distance to its edge; in a left-anchored
    // movie (the lane) its left stays put and its right reaches the stage's right edge, a 9-sliced one's caps unwidened;
    // a left clip in a right-anchored movie still holds the left edge; cover scales evenly.
    [Fact]
    public void StretchKeepsMarginsAndSlices()
    {
        var before = LumenStage.Width;
        try
        {
            LumenStage.Width = 1680;
            var background = package("""{ "character": "bg", "anchor": "stretch", "keys": [ { "frame": 0 } ] }""",
                """ "bg": { "texture": "t", "rect": [0, 0, 1280, 720] } """);
            Assert.Equal([-200f, 1480f], edges(background));
            var lane = package("""{ "character": "band", "anchor": "stretch", "keys": [ { "frame": 0 } ] }""",
                """ "band": { "texture": "t", "rect": [400, 0, 880, 100], "slice": [20, 0, 20, 0] } """);
            lane.SceneAnchor = LumenAnchor.Left;
            Assert.Equal([400f, 420f, 1660f, 1680f], edges(lane));
            var corner = package("""{ "character": "dot", "anchor": "left", "keys": [ { "frame": 0, "x": 10 } ] }""",
                """ "dot": { "texture": "t", "rect": [0, 0, 10, 10] } """);
            corner.SceneAnchor = LumenAnchor.Right;
            Assert.Equal([-390f, -380f], edges(corner)); // -200 for the left edge, -200 against the movie's +200
            var cover = LumenStage.Anchored(LumenMatrix.Identity, LumenAnchor.Cover);
            Assert.Equal((1.3125f, -200f, -112.5f), (cover.M11, cover.X, cover.Y));
        }
        finally
        {
            LumenStage.Width = before;
        }
    }
}
