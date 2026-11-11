using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Package;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Tests;

public sealed class MoviePackageTests
{
    private const string Movie = """
        {
          "format": 1,
          "frameRate": 60,
          "root": "main",
          "textures": { "ball": "textures/ball.png" },
          "shapes": { "ball": { "texture": "ball", "rect": [-10, -10, 20, 20] } },
          "sprites": {
            "main": {
              "frames": 6,
              "labels": { "in": 0, "out": 4 },
              "layers": [
                { "character": "ball", "name": "ball", "until": 5,
                  "keys": [ { "frame": 0, "x": 100, "y": 50, "alpha": 0 }, { "frame": 4, "x": 300, "y": 50 } ] }
              ]
            }
          }
        }
        """;

    private static LumenPlayer play(string json)
    {
        var assembled = MoviePackage.Assemble(json);
        Assert.Equal("textures/ball.png", Assert.Single(assembled.TextureFiles));
        var read = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb),
            validationContext: new LmbSemanticValidationContext(assembled.TextureFiles.Length));
        Assert.Empty(read.Diagnostics);
        return new LumenPlayer(read.Value, 1280, 720);
    }

    [Fact]
    public void AssembledMoviePlaysItsTweenLabelsAndRemoval()
    {
        var player = play(Movie);
        Assert.Equal(6, player.FrameCount);
        Assert.Equal(4, player.Labels["out"]);
        var first = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal((90f, 40f, 0f), (first.TopLeft.X, first.TopLeft.Y, first.MultiplyColor.Alpha));

        player.Advance();
        player.Advance();
        var middle = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal((190f, 0.5f), (middle.TopLeft.X, middle.MultiplyColor.Alpha));
        Assert.True(player.TryGetInstanceTransform("ball", out var transform));
        Assert.Equal(200f, transform.X);

        player.Advance();
        player.Advance();
        Assert.Equal(1f, Assert.Single(player.CreateRenderSnapshot().Quads).MultiplyColor.Alpha);
        player.Advance();
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        player.Advance(); // Loops to frame 0: placed again.
        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Empty(player.Diagnostics);
    }

    [Fact]
    public void TheInspectorSeesPicksAndMovesClipsWithoutTouchingTheEdgeLog()
    {
        var edges = new StringWriter();
        LumenEdgeLog.Writer = edges;
        var player = play(Movie);
        player.Name = "inspected";
        var clips = player.InspectDisplayList();
        Assert.Equal(["", "ball"], clips.Select(static clip => clip.Name == "_level0" ? "" : clip.Name));
        var ball = clips[1];
        Assert.Equal((LumenInstanceKind.Shape, 1, 100f, 0f), (ball.Kind, ball.Level, ball.Transform.X, ball.Multiply.Alpha));
        Assert.Equal((LumenInstanceKind.Sprite, 6, "in"), (clips[0].Kind, clips[0].FrameCount, clips[0].Label));
        Assert.Equal([("in", 0), ("out", 4)], player.InspectLabels(""));
        Assert.True(player.TryInspectBounds(ball.Path, out var box));
        Assert.Equal((90f, 40f, 20f, 20f), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal(ball.Path, player.InspectPick(100, 50));
        Assert.Null(player.InspectPick(500, 500));

        Assert.True(player.InspectGoto("", 4, play: false));
        Assert.Equal((4, 300f), (player.CurrentFrame, player.InspectDisplayList()[1].Transform.X));
        Assert.True(player.InspectSetHidden(ball.Path, true));
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.False(player.InspectGoto("999", 0, play: true));
        LumenEdgeLog.Writer = null;
        Assert.DoesNotContain("\tinspected\t", edges.ToString());
    }

    [Theory]
    [InlineData("\"root\": \"main\"", "\"root\": \"nope\"", "names no sprite")]
    [InlineData("\"character\": \"ball\"", "\"character\": \"cube\"", "unknown character")]
    [InlineData("\"frame\": 4,", "\"frame\": 9,", "inside the sprite's 6 frames")]
    [InlineData("\"format\": 1", "\"format\": 2", "\"format\" must be 1")]
    public void MistakesAreReportedByName(string from, string to, string expected) =>
        Assert.Contains(expected, Assert.Throws<FormatException>(() => MoviePackage.Assemble(Movie.Replace(from, to))).Message);
}
