namespace Waddamburo.Editor.Tests;

public sealed class TimelineTrackRowTests
{
    [Fact]
    public void ExpandedCurvesKeepLaterKeyRowsAndBoxSelectionInTheRightPlace()
    {
        var rows = TimelineTrackRow.Arrange(["camera.x", "don.yaw", "notes.drunk"], new HashSet<string> { "camera.x", "notes.drunk" });
        Assert.Equal(16, rows[0].KeyCenter);
        Assert.Equal(204, rows[1].KeyCenter);
        Assert.Equal(236, rows[2].KeyCenter);
        Assert.DoesNotContain(rows, row => row.ContainsHeader(100)); // camera's graph, rather than a key row
        Assert.Equal("don.yaw", Assert.Single(rows, row => row.ContainsHeader(204)).Track);
        var collapsed = TimelineTrackRow.Arrange(rows.Select(r => r.Track), new HashSet<string>());
        Assert.Equal(48, collapsed[1].KeyCenter);
        Assert.All(collapsed, row => Assert.Equal(0, row.GraphHeight));
    }
}
