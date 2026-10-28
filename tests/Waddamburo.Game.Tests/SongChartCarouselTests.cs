using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class SongChartCarouselTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(101)]
    public void EveryChartCanBeReachedAndTheWindowStaysInsideTheList(int count)
    {
        var carousel = new SongChartCarousel(song(count), [0]);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(index + 3, carousel.Selected(0));
            Assert.Equal($"oni-{index}", carousel.Choice(0).Key.StableId);
            Assert.InRange(carousel.Selected(0) - carousel.WindowStart, 0, SongChartCarousel.VisibleCount - 1);
            Assert.InRange(carousel.WindowStart, 0, Math.Max(0, carousel.Slots.Length - SongChartCarousel.VisibleCount));
            carousel.Move(0, 1);
        }
        carousel.Move(0, int.MaxValue);
        Assert.Equal(count + 2, carousel.Selected(0));
        carousel.Move(0, int.MinValue);
        Assert.Equal(3, carousel.Selected(0));
        Assert.Equal(0, carousel.WindowStart);
    }

    [Fact]
    public void TwoPlayersConfirmDifferentChartsAndCanUndoTheirDecision()
    {
        var carousel = new SongChartCarousel(song(12), [0, 1]);
        carousel.Move(0, 7);
        carousel.Move(1, 10);
        carousel.Confirm(0);
        Assert.False(carousel.Ready);
        carousel.Move(0, -5);
        Assert.Equal(10, carousel.Selected(0));
        carousel.Confirm(1);
        Assert.True(carousel.Ready);
        Assert.NotEqual(carousel.Choice(0).Key, carousel.Choice(1).Key);
        carousel.UndoConfirm(0);
        Assert.False(carousel.Ready);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongChartCarousel(song(1), [2]));
    }

    [Fact]
    public void BothPlayersStayVisibleWhenTheirChoicesFitInOneWindow()
    {
        var carousel = new SongChartCarousel(song(12), [0, 1]);
        carousel.Move(1, 3);
        Assert.Equal(3, carousel.WindowStart);
        carousel.Move(0, 1);
        foreach (var side in carousel.Sides)
            Assert.InRange(carousel.Selected(side) - carousel.WindowStart, 0, SongChartCarousel.VisibleCount - 1);
        carousel.Move(0, -1);
        Assert.Equal(3, carousel.WindowStart);
    }

    [Fact]
    public void EnteringSettingsDoesNotPullTheOtherPlayersChartOffscreen()
    {
        var carousel = new SongChartCarousel(song(12), [0, 1]);
        carousel.Move(1, 10);
        carousel.EnterUtilities(0);
        Assert.InRange(carousel.Selected(1) - carousel.WindowStart, 0, SongChartCarousel.VisibleCount - 1);
    }

    [Fact]
    public void ReturningFromAdditionalChartsAllowsNativeUtilitiesWithoutLosingTheChart()
    {
        var carousel = new SongChartCarousel(song(12), [0, 1]);
        carousel.Move(0, 10);
        Assert.False(carousel.CanEnterUtilities(0));
        carousel.Move(0, -10);
        Assert.True(carousel.CanEnterUtilities(0));
        carousel.EnterUtilities(0);
        Assert.True(carousel.IsUtility(0));
        Assert.False(carousel.IsUtility(1));
        Assert.Equal(0, carousel.WindowStart);
        carousel.LeaveUtilities(0);
        carousel.Move(0, 1);
        Assert.Equal("oni-1", carousel.Choice(0).Key.StableId);
        Assert.False(carousel.IsUtility(0));
    }

    private static SongDescriptor song(int charts)
    {
        var key = new SongKey(SongSourceKind.Tja, "carousel");
        return new SongDescriptor(key, new SongTitle("Carousel"), null,
            Enumerable.Range(0, charts).Select(index => new SongChartDescriptor(new ChartKey(key, $"oni-{index}"),
                $"Inner Oni {index}", new CatalogAssetKey(new("synthetic"), $"chart-{index}"), TaikoCourse.Oni, 9)));
    }
}
