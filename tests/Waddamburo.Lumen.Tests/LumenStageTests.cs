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
            LumenStage.Width = LumenStage.Fit(4d / 3);
            Assert.Equal((960f, 1040f), (LumenStage.Width, LumenStage.Anchored(at, LumenAnchor.Right).X)); // the right edge at 1120: 80 in
            Assert.Equal(LumenStage.MaxWidth, LumenStage.Fit(32d / 9));
        }
        finally
        {
            LumenStage.Width = before;
        }
    }
}
