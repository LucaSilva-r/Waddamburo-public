namespace Waddamburo.Editor.Tests;

public sealed class TimelineLabelLayoutTests
{
    [Fact]
    public void CrowdedNamesNeverOverlapAndOverflowKeepsEveryLabel()
    {
        (string, int, float)[] source = [("start", 0, 70), ("middle", 1, 90), ("middle_long", 2, 120), ("end", 3, 70), ("later", 100, 60)];
        var placed = TimelineLabelLayout.Arrange(source, 4, 500, maxRows: 3);
        Assert.Equal(source.Length, placed.Count);
        Assert.Contains(placed, label => label.Row == -1);
        Assert.All(placed.Where(label => label.Row >= 0).GroupBy(label => label.Row), row =>
        {
            var ordered = row.OrderBy(label => label.Left).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                Assert.True(ordered[i].Left >= ordered[i - 1].Left + ordered[i - 1].Width + 4);
        });
        Assert.Equal(0, placed.Single(label => label.Name == "later").Row);
    }

    [Fact]
    public void EndLabelsStayInsideTheTrackAndSameFrameNamesAreStable()
    {
        var placed = TimelineLabelLayout.Arrange([("z", 99, 80), ("a", 99, 80), ("long", 100, 300)], 4, 400);
        Assert.Equal(["a", "z", "long"], placed.Select(label => label.Name));
        Assert.All(placed, label => Assert.InRange(label.Left + label.Width, 0, 400));
        Assert.Equal([0, 1, 2], placed.Select(label => label.Row));
        Assert.Empty(TimelineLabelLayout.Arrange([], 4, 400));
    }
}
