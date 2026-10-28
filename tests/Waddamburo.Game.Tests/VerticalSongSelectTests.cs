using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.Tests;

public sealed class VerticalSongSelectTests
{
    [Fact]
    public void FolderPreviewWaitsForTheVisibleFrameAndReplaysOnEveryVisit()
    {
        var transition = new SongFolderTransition();
        for (var visit = 0; visit < 3; visit++)
        {
            var lastExpansion = 0f;
            for (var tick = 0; tick < SongFolderTransition.Duration; tick++)
            {
                transition.Advance(visit, true);
                // Different native phases must not change a newly opening preview.
                var cold = transition.PreviewMotion(new(.2f, 0));
                var warm = transition.PreviewMotion(new(1, 1));
                if (transition.Progress < 1) Assert.Equal(cold, warm);
                if (transition.Progress <= .6f) Assert.Equal(0, warm.Expansion);
                if (transition.Progress <= .55f) Assert.Equal(0, transition.SongContentOpacity(1));
                if (transition.Progress >= .6f) Assert.Equal(1, transition.SongContentOpacity(1));
                Assert.InRange(warm.Expansion, lastExpansion, 1);
                lastExpansion = warm.Expansion;
            }
            Assert.Equal(new NativeSongMotion(.25f, .5f), transition.PreviewMotion(new(.25f, .5f)));
            transition.Close();
            Assert.Equal(new NativeSongMotion(1, 1), transition.PreviewMotion(new(1, 1)));
            for (var tick = 0; tick <= SongFolderTransition.Duration; tick++)
            {
                transition.Advance(-1, false);
                Assert.Equal(new NativeSongMotion(1, 1), transition.PreviewMotion(new(1, 1)));
            }
            Assert.Equal(-1, transition.Folder);
        }
    }

    [Theory]
    [InlineData(11, 2)]
    [InlineData(27, 2)]
    [InlineData(27, 13)]
    [InlineData(64, 3)]
    public void OpeningAfterCategoryWrapKeepsTheFirstSongInFocusWhileItsPeriodChanges(int size, int genre)
    {
        var browser = new VerticalSongBrowser();
        var closed = new bool[size];var opened = new bool[size];opened[genre] = true;
        var counts = Enumerable.Repeat(5, size).ToArray();counts[genre] = 101;
        browser.Advance(closed, counts, size - 1, -1, 246, motion: new(1, 0));
        browser.Advance(closed, counts, 0, -1, 246, motion: new(1, 0));
        browser.Advance(closed, counts, genre, -1, 246, motion: new(1, 0));
        for (var tick = 0; tick < 40; tick++)
        {
            var expansion = Math.Clamp((tick - 18) / 21f, 0, 1);
            browser.Advance(opened, counts, genre, 1, 246, motion: new(1, expansion));
            Assert.Equal(288, browser.SelectionTop - browser.Camera + 25.35f
                + (browser.SelectionHeight - 65) / 2, 2);
            Assert.Equal(VerticalSongBrowser.SongRowPitch + browser.VisibleRowExpansion(0),
                browser.VisibleRowTop(1) - browser.VisibleRowTop(0), 2);
        }
    }

    [Fact]
    public void CategoryPreviewSharesTheRenderedSongApertureAndItsCentre()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false], [84, 47], 0, -1, 246);
        Assert.Equal(234, browser.BodyHeight(0));
        Assert.Equal(288, browser.Top(0) - browser.Camera + browser.BodyHeight(0) / 2);
        for (var tick = 0; tick < 80; tick++) browser.Advance([true, false], [84, 47], 0, 1, 246, motion: new(1, 1));
        Assert.Equal(234, 53 + browser.SelectionHeight - 65);
        Assert.Equal(288, browser.SelectionTop - browser.Camera + 25.35f + 181 / 2f, 3);
    }

    [Fact]
    public void GapsShrinkTenPercentWhileCategoryDepthDropsOnePerCircularNeighbour()
    {
        Assert.Equal((74.75f - 58.35f) * .9f, VerticalSongBrowser.SongRowPitch - 58.35f, 3);
        Assert.Equal((72 - 58.35f) * .9f, VerticalSongBrowser.CategoryPitch - 58.35f, 3);
        var browser = new VerticalSongBrowser();
        var closed = new bool[9]; var counts = Enumerable.Repeat(5, 9).ToArray();
        foreach (var genre in new[] { 0, 4, 8 })
        {
            browser.Advance(closed, counts, genre, -1, 246);
            Assert.Equal(0, browser.CategoryZ(genre));
            for (var step = 1; step <= 4; step++)
            {
                Assert.Equal(-step, browser.CategoryZ((genre + step) % 9));
                Assert.Equal(-step, browser.CategoryZ((genre - step + 9) % 9));
            }
            Assert.Equal(-9, browser.CategoryZ(genre, -1));
            Assert.Equal(-9, browser.CategoryZ(genre, 1));
        }
    }
    [Fact]
    public void PreviousSongClosesInPlaceBeforeNextSongOpensAndItsNeighboursNeverOverlap()
    {
        var browser = new VerticalSongBrowser();
        for (var tick = 0; tick < 80; tick++) browser.Advance([true], [12], 0, 2, 246);
        Assert.Equal(181, browser.VisibleRowExpansion(0));
        var remaining = 181f;
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
        {
            browser.Advance([true], [12], 0, 3, 246);
            var closing = browser.VisibleRowExpansion(-1);
            Assert.InRange(closing, 0, remaining);
            Assert.Equal(VerticalSongBrowser.SongRowPitch + closing,
                browser.VisibleRowTop(0) - browser.VisibleRowTop(-1), 3);
            Assert.Equal(65, browser.SelectionHeight);
            remaining = closing;
        }
        Assert.Equal(0, remaining);
        for (var tick = 0; tick < 60; tick++) browser.Advance([true], [12], 0, 3, 246);
        Assert.Equal(246, browser.SelectionHeight);
        Assert.Equal(VerticalSongBrowser.SongRowPitch + 181,
            browser.VisibleRowTop(1) - browser.VisibleRowTop(0), 3);
        Assert.Equal(0, browser.VisibleRowExpansion(12));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(101)]
    public void RepeatedSongRowsStayOrderedDuringRapidReversalAndWrap(int count)
    {
        var browser = new VerticalSongBrowser();
        var board = 0;
        for (var tick = 0; tick < 80; tick++) browser.Advance([true], [count], 0, board, 246);
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick % 5 == 0) board = (board + (tick / 50 % 2 == 0 ? 1 : count - 1)) % count;
            browser.Advance([true], [count], 0, board, 246);
            for (var offset = -8; offset < 8; offset++)
                Assert.Equal(VerticalSongBrowser.SongRowPitch + browser.VisibleRowExpansion(offset),
                    browser.VisibleRowTop(offset + 1) - browser.VisibleRowTop(offset), 2);
        }
    }

    [Fact]
    public void OpeningACategoryExpandsTheFirstSongWithoutAnotherHoverDelay()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false], [12, 3], 0, -1, 246);
        browser.Advance([true, false], [12, 3], 0, 1, 246);
        Assert.InRange(browser.SelectionHeight, 65.01f, 245.99f);
        for (var tick = 1; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([true, false], [12, 3], 0, 1, 246);
        Assert.Equal(246, browser.SelectionHeight);
    }

    [Fact]
    public void OniAndUraPreviewSwitchTogetherAndResetForTheNextSong()
    {
        var cycle = new SongPreviewCycle();
        cycle.Advance("a", 6, 10);
        Assert.False(cycle.ShowsUra); Assert.Equal(6, cycle.Level);
        for (var tick = 0; tick < SongPreviewCycle.HoldTicks - 7; tick++) cycle.Advance("a", 6, 10);
        Assert.False(cycle.ShowsUra); Assert.InRange(cycle.Opacity, .01f, .99f);
        for (var tick = 0; tick < 7; tick++) cycle.Advance("a", 6, 10);
        Assert.True(cycle.ShowsUra); Assert.Equal(10, cycle.Level); Assert.Equal(0, cycle.Opacity);
        for (var tick = 0; tick < SongPreviewCycle.FadeTicks; tick++) cycle.Advance("a", 6, 10);
        Assert.Equal(1, cycle.Opacity);
        cycle.Advance("b", 8, null);
        for (var tick = 0; tick < 400; tick++) cycle.Advance("b", 8, null);
        Assert.False(cycle.ShowsUra); Assert.Equal(8, cycle.Level); Assert.Equal(1, cycle.Opacity);
        cycle.Advance("ura-only", null, 9);
        Assert.True(cycle.ShowsUra); Assert.Equal(9, cycle.Level); Assert.Equal(1, cycle.Opacity);
    }

    [Fact]
    public void SongHighlightTravelsWithItsRowUsingTheCategoryCamera()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([true, false, false], [30, 2, 2], 0, 2, 246);
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
        {
            browser.Advance([true, false, false], [30, 2, 2], 0, 3, 246);
            Assert.Equal(browser.BoardTop(0, 3), browser.SelectionTop);
            Assert.Equal(65, browser.SelectionHeight);
        }
        Assert.Equal(browser.BoardTop(0, 3), browser.SelectionTop);
        Assert.Equal(262.65f, browser.SelectionTop - browser.Camera, 3);
    }

    [Fact]
    public void SongSeamShowsTheLastRowAboveReturnAndReversesWithoutCrossingTheWholeCategory()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([true, false], [101, 2], 0, 0, 65);
        Assert.Equal(VerticalSongBrowser.SongRowPitch, browser.BoardTop(0, 0) - browser.BoardTop(0, 100), 3);
        var camera = browser.Camera;
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([true, false], [101, 2], 0, 100, 246);
        Assert.Equal(camera - VerticalSongBrowser.SongRowPitch, browser.Camera, 3);
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([true, false], [101, 2], 0, 0, 65);
        Assert.Equal(camera, browser.Camera, 3);
    }

    [Fact]
    public void CategoryWrapUsesEquivalentCopiesWithoutACrossScreenTween()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false, false], [4, 4, 4], 0, -1, 246);
        var before = browser.Top(2) - browser.Camera;
        browser.Advance([false, false, false], [4, 4, 4], 1, -1, 246);
        var after = browser.Top(2) - browser.Camera;
        Assert.InRange(Math.Abs(MathF.IEEERemainder(after - before, browser.Length)), 0, 20);
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([false, false, false], [4, 4, 4], 1, -1, 246);
        Assert.Equal(171, browser.Top(1) - browser.Camera, 3);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(36)]
    [InlineData(64)]
    public void RapidCategoryChangesAndReversalsKeepMotionBoundedAndSettleOnTheSelection(int categoryCount)
    {
        var browser = new VerticalSongBrowser();
        var opened = new bool[categoryCount]; var counts = Enumerable.Repeat(10, categoryCount).ToArray();
        var genre = 0;
        browser.Advance(opened, counts, genre, -1, 246);
        for (var tick = 0; tick < 1200; tick++)
        {
            if (tick % 2 == 0) genre = (genre + (tick / 60 % 2 == 0 ? 1 : categoryCount - 1)) % categoryCount;
            var camera = browser.Camera;
            var positions = Enumerable.Range(0, categoryCount).Select(i => browser.Top(i) - camera).ToArray();
            browser.Advance(opened, counts, genre, -1, 246);
            Assert.InRange(Math.Abs(browser.Camera - camera), 0, 80);
            for (var i = 0; i < categoryCount; i++)
            {
                var movement = MathF.IEEERemainder(browser.Top(i) - browser.Camera - positions[i], browser.Length);
                Assert.InRange(Math.Abs(movement), 0, 120);
                Assert.InRange(browser.Preview(i), 0, 1);
            }
        }
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance(opened, counts, genre, -1, 246);
        Assert.Equal(171, browser.Top(genre) - browser.Camera, 3);
    }

    [Fact]
    public void CategoryNavigationFollowsTheNativeSlideWithoutASecondEasingTail()
    {
        var browser = new VerticalSongBrowser();
        var closed = new bool[9]; var counts = Enumerable.Repeat(5, 9).ToArray();
        browser.Advance(closed, counts, 0, -1, 246, motion: new(1, 0));
        foreach (var slide in new[] { 0f, .4375f, .75f, .9375f, 1f })
        {
            browser.Advance(closed, counts, 1, -1, 246, motion: new(slide, 0));
            Assert.Equal(slide, browser.Preview(1), 4);
            Assert.Equal(1 - slide, browser.Preview(0), 4);
            Assert.Equal(VerticalSongBrowser.CompactRowHeight
                + (VerticalSongBrowser.PreviewBodyHeight - VerticalSongBrowser.CompactRowHeight) * slide,
                browser.BodyHeight(1), 3);
        }
        var top = browser.Top(1) - browser.Camera;
        Assert.Equal(VerticalSongBrowser.CategoryFocusTop, top, 3);
        for (var tick = 0; tick < 30; tick++)
        {
            browser.Advance(closed, counts, 1, -1, 246, motion: new(1, 0));
            Assert.Equal(top, browser.Top(1) - browser.Camera);
        }
    }

    [Fact]
    public void CategorySlideCanBeInterruptedAndFastForwardedWithoutResidualExpansion()
    {
        var browser = new VerticalSongBrowser();
        var closed = new bool[9]; var counts = Enumerable.Repeat(5, 9).ToArray();
        browser.Advance(closed, counts, 0, -1, 246, motion: new(1, 0));
        browser.Advance(closed, counts, 1, -1, 246, motion: new(.75f, 0));
        var top = browser.Top(1) - browser.Camera;
        var height = browser.BodyHeight(1);
        browser.Advance(closed, counts, 0, -1, 246, motion: new(0, 0));
        Assert.Equal(top, browser.Top(1) - browser.Camera, 3);
        Assert.Equal(height, browser.BodyHeight(1));
        browser.Advance(closed, counts, 0, -1, 246, motion: new(1, 0));
        Assert.Equal(1, browser.Preview(0));
        Assert.Equal(0, browser.Preview(1));
        Assert.Equal(VerticalSongBrowser.CompactRowHeight, browser.BodyHeight(1), 3);
        Assert.Equal(VerticalSongBrowser.CategoryFocusTop, browser.Top(0) - browser.Camera, 3);
    }

    [Fact]
    public void CategoryHoverUsesOneFiniteClockWithoutCameraJumpsOrAnElasticTail()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false, false], [4, 4, 4], 0, -1, 246);
        var previous = browser.Top(1) - browser.Camera;
        var samples = new List<float>();
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
        {
            // Native artwork may report a different height every tick: geometry must ignore it.
            browser.Advance([false, false, false], [4, 4, 4], 1, -1, tick % 2 == 0 ? 65 : 246, [65, 65 + tick, 65]);
            var current = browser.Top(1) - browser.Camera;
            Assert.InRange(Math.Abs(current - previous), 0, 35);
            samples.Add(current); previous = current;
        }
        Assert.Equal(171, samples[^1], 3);
        var settled = browser.Top(1) - browser.Camera;
        for (var tick = 0; tick < 60; tick++) browser.Advance([false, false, false], [4, 4, 4], 1, -1, 246);
        Assert.Equal(settled, browser.Top(1) - browser.Camera);
    }

    [Fact]
    public void OpeningFolderAndReversingMidAnimationKeepTheCurrentTopContinuous()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false, false], [12, 4, 4], 0, -1, 246);
        var previousHeight = browser.BodyHeight(0);
        for (var tick = 0; tick < SongPreviewCycle.FadeTicks; tick++)
        {
            browser.Advance([true, false, false], [12, 4, 4], 0, 0, 65);
            Assert.True(browser.BodyHeight(0) >= previousHeight);
            previousHeight = browser.BodyHeight(0);
        }
        var top = browser.Top(0) - browser.Camera;
        browser.Advance([false, false, false], [12, 4, 4], 0, -1, 246);
        Assert.InRange(Math.Abs(browser.Top(0) - browser.Camera - top), 0, 2);
        for (var tick = 0; tick < 30; tick++) browser.Advance([false, false, false], [12, 4, 4], 0, -1, 246);
        Assert.Equal(234, browser.BodyHeight(0));
    }

    [Fact]
    public void ExpandedCategoryReservesSpaceForItsHeaderAboveTheBody()
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([false, false, false], [2, 2, 2], 0, -1, 246);
        var headerTop = browser.Top(0) - 46;
        Assert.True(browser.BoardTop(2, -1) + 52 < headerTop);
        Assert.True(browser.BoardTop(1, -1) > browser.Top(0) + 234);
    }

    [Fact]
    public void LeavingFolderKeepsItsFullHeightAndOtherOpenFolders()
    {
        var browser = new VerticalSongBrowser();
        for (var tick = 0; tick < 30; tick++) browser.Advance([true, false, true], [100, 2, 4], 0, 99, 246);
        Assert.Equal(VerticalSongBrowser.SongRowPitch, browser.BoardTop(0, 99) - browser.BoardTop(0, 98), 3);
        Assert.True(browser.Top(0) < browser.BoardTop(0, 99));
        for (var tick = 0; tick < 30; tick++) browser.Advance([true, false, true], [100, 2, 4], 1, -1, 65);
        Assert.Equal(7395.635f, browser.Height(0), 2);
        Assert.Equal(377.075f, browser.Height(2), 3);
        Assert.Equal(1, browser.Reveal(0));
        Assert.Equal(1, browser.Reveal(2));
    }

    [Fact]
    public void ExpandedPreviewPushesFollowingRowsAndClosingAnchorsTheTop()
    {
        var browser = new VerticalSongBrowser();
        for (var tick = 0; tick <= VerticalSongBrowser.PreviewDelayTicks + VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([true, false], [8, 1], 0, 2, 246);
        Assert.Equal(VerticalSongBrowser.SongRowPitch + 181, browser.BoardTop(0, 3) - browser.BoardTop(0, 2), 3);
        var height = browser.Height(0);
        browser.Advance([false, false], [8, 1], 0, -1, 65);
        Assert.True(browser.Height(0) < height);
        Assert.InRange(browser.Reveal(0), 0, 1);
        for (var tick = 0; tick < 30; tick++) browser.Advance([false, false], [8, 1], 0, -1, 65);
        Assert.Equal(290.635f, browser.Height(0), 3);
    }

    [Fact]
    public void MovingAcrossSongsRestartsThePreviewDelay()
    {
        var browser = new VerticalSongBrowser();
        for (var board = 1; board <= 4; board++)
            for (var tick = 0; tick < VerticalSongBrowser.PreviewDelayTicks; tick++)
            {
                browser.Advance([true, false], [8, 1], 0, board, 246);
                Assert.Equal(65, browser.SelectionHeight);
                Assert.Equal(VerticalSongBrowser.SongRowPitch, browser.BoardTop(0, board + 1) - browser.BoardTop(0, board), 3);
            }
        for (var tick = 0; tick < VerticalSongBrowser.TransitionTicks; tick++)
            browser.Advance([true, false], [8, 1], 0, 4, 246);
        Assert.Equal(246, browser.SelectionHeight);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void SongNavigationUsesTheCategorySlideForBothAperturesAndCamera(int direction)
    {
        var songs = new VerticalSongBrowser();
        var categories = new VerticalSongBrowser();
        for (var tick = 0; tick < 80; tick++)
        {
            songs.Advance([true], [20], 0, 5, 246, motion: new(1, 1));
            categories.Advance([false, false, false], [20, 20, 20], 1, -1, 246, motion: new(1, 0));
        }
        var camera = songs.Camera;
        var oldTop = songs.SelectionTop - camera;
        var nextTop = songs.VisibleRowTop(direction) - camera;
        foreach (var slide in new[] { 0f, .2344f, .4375f, .6094f, .75f, .8594f, .9375f, .9844f, 1f })
        {
            songs.Advance([true], [20], 0, 5 + direction, 246, direction: direction, motion: new(slide, 0));
            categories.Advance([false, false, false], [20, 20, 20], 1 + direction, -1, 246, motion: new(slide, 0));
            Assert.Equal(categories.Preview(1 + direction), songs.VisibleRowExpansion(0) / 181, 4);
            Assert.Equal(categories.Preview(1), songs.VisibleRowExpansion(-direction) / 181, 4);
            Assert.InRange(Math.Abs(camera + direction * VerticalSongBrowser.SongRowPitch * slide - songs.Camera), 0, .001f);
            Assert.Equal(65 + 181 * slide, songs.SelectionHeight, 3);
            if (slide == 0)
            {
                Assert.Equal(oldTop, songs.VisibleRowTop(-direction) - songs.Camera, 3);
                Assert.Equal(nextTop, songs.SelectionTop - songs.Camera, 3);
            }
            for (var offset = -3; offset < 3; offset++)
                Assert.InRange(Math.Abs(VerticalSongBrowser.SongRowPitch + songs.VisibleRowExpansion(offset)
                    - (songs.VisibleRowTop(offset + 1) - songs.VisibleRowTop(offset))), 0, .001f);
        }
        Assert.Equal(288, songs.SelectionTop - songs.Camera + 25.35f + 181 / 2f, 3);
        var settled = songs.Camera;
        // The movie opens afterwards. The host must already be settled.
        foreach (var expansion in new[] { 0f, .125f, .5f, 1f })
        {
            songs.Advance([true], [20], 0, 5 + direction, 246, motion: new(1, expansion));
            Assert.Equal(settled, songs.Camera);
            Assert.Equal(246, songs.SelectionHeight);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(101)]
    public void SongSlideReversalsAndWrapKeepEveryVisibleCardContinuous(int count)
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([true], [count], 0, 0, 246, motion: new(1, 1));
        var selected = 0;
        for (var step = 0; step < 60; step++)
        {
            var direction = step % 3 == 0 ? -1 : 1;
            var positions = Enumerable.Range(-4, 9).ToDictionary(i => browser.SelectedUnwrappedBoard + i,
                i => (Top: browser.VisibleRowTop(i) - browser.Camera, Expansion: browser.VisibleRowExpansion(i)));
            selected = (selected + direction + count) % count;
            browser.Advance([true], [count], 0, selected, 246, direction: direction, motion: new(0, 0));
            for (var offset = -3; offset <= 3; offset++)
            {
                var before = positions[browser.SelectedUnwrappedBoard + offset];
                Assert.Equal(before.Top, browser.VisibleRowTop(offset) - browser.Camera, 2);
                Assert.Equal(before.Expansion, browser.VisibleRowExpansion(offset), 3);
            }
            // Interrupt again before completing, then occasionally fast-forward.
            browser.Advance([true], [count], 0, selected, 246, direction: direction,
                motion: new(step % 4 == 0 ? 1 : .75f, 0));
        }
        browser.Advance([true], [count], 0, selected, 246, motion: new(1, 0));
        Assert.Equal(246, browser.SelectionHeight);
        Assert.Equal(288, browser.SelectionTop - browser.Camera + 25.35f + 181 / 2f, 2);
        Assert.Equal(0, browser.VisibleRowExpansion(count));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public void PageSkipsKeepRowsAcrossTheWholeViewportWhileTheCursorIsStillOffScreen(int step)
    {
        var browser = new VerticalSongBrowser();
        browser.Advance([true], [101], 0, 50, 246, motion: new(1, 1));
        foreach (var slide in new[] { 0f, .2344f, .4375f, .6094f, .75f, 1f })
        {
            browser.Advance([true], [101], 0, 50 + step, 246, direction: Math.Sign(step), motion: new(slide, 0));
            var offsets = browser.VisibleSongOffsets().ToArray();
            Assert.True(offsets.Length <= 23);
            Assert.True(browser.VisibleRowTop(offsets[0]) - browser.Camera <= 0);
            Assert.True(browser.VisibleRowTop(offsets[^1]) - browser.Camera
                + VerticalSongBrowser.SongRowPitch + browser.VisibleRowExpansion(offsets[^1]) >= 720);
            Assert.Equal(offsets.Length, offsets.Select(i => ((browser.SelectedUnwrappedBoard + i) % 23 + 23) % 23).Distinct().Count());
        }
    }

    [Fact]
    public void TwoRowSeamKeepsDirectionWithoutExpandingAnotherOccurrenceOfTheSameSong()
    {
        var browser = new VerticalSongBrowser();
        for (var tick = 0; tick < 80; tick++) browser.Advance([true], [2], 0, 0, 246);
        for (var selected = 1; selected <= 8; selected++)
        {
            for (var tick = 0; tick < 80; tick++) browser.Advance([true], [2], 0, selected % 2, 246, direction: 1);
            Assert.Equal(selected, browser.SelectedUnwrappedBoard);
            Assert.Equal(181, browser.VisibleRowExpansion(0));
            Assert.Equal(0, browser.VisibleRowExpansion(-2));
            Assert.Equal(0, browser.VisibleRowExpansion(2));
            Assert.Equal(288, browser.SelectionTop - browser.Camera + 25.35f + 181 / 2f, 3);
        }
    }

    [Fact]
    public void FolderReactsImmediatelyAndWaitsForRowsOnlyAtTheHandoff()
    {
        var transition = new SongFolderTransition();
        transition.Advance(2, false);
        Assert.True(transition.Progress > 0);
        Assert.True(transition.CategoryWidth(1) < 1);
        for (var tick = 0; tick < 35; tick++) transition.Advance(2, false);
        Assert.Equal(1, transition.OtherAlpha);
        Assert.Equal(0, transition.RowsAlpha);
        Assert.InRange(transition.Progress, .25f, .299f);
        Assert.True(transition.CategoryVisible);
        Assert.True(transition.CategoryWidth(1) > 0);
        Assert.True(transition.Busy);
        for (var tick = 0; tick < 20; tick++)
        {
            transition.Advance(2, true);
            Assert.True(transition.OtherAlpha is 0 or 1);
            Assert.True(transition.RowsAlpha is 0 or 1);
        }
        for (var tick = 0; tick < 80; tick++) transition.Advance(2, true);
        Assert.Equal(2, transition.Folder);
        Assert.Equal(1, transition.RowsAlpha);
        Assert.False(transition.Busy);
        transition.Close();
        for (var tick = 0; tick <= SongFolderTransition.Duration; tick++) transition.Advance(-1, false);
        Assert.Equal(-1, transition.Folder);
        Assert.Equal(1, transition.OtherAlpha);
        Assert.Equal(0, transition.OtherOffset);
    }

    [Fact]
    public void FolderHandoffRetainsBothInterpolationEndpointsAndNeverOverlapsFullCards()
    {
        var transition = new SongFolderTransition();
        for (var tick = 0; tick < SongFolderTransition.Duration; tick++)
        {
            transition.Advance(2, true);
            check();
        }
        Assert.Equal(1, transition.Progress);
        transition.Close();
        for (var tick = 0; tick < MathF.Ceiling(SongFolderTransition.ReturnDuration); tick++)
        {
            transition.Advance(-1, false);
            check();
        }
        Assert.Equal(0, transition.Progress);
        Assert.Equal(2, transition.Folder);
        transition.Advance(-1, false);
        Assert.Equal(-1, transition.Folder);

        void check()
        {
            foreach (var fraction in new[] { 0f, .25f, .5f, .75f, 1f })
            {
                var category = transition.CategoryWidth(fraction);
                var song = transition.SongWidth(fraction);
                Assert.True(category == 0 || song == 0);
                if (category > 0) Assert.True(transition.CategoryVisible);
                if (song > 0) Assert.Equal(1, transition.RowsAlpha);
            }
        }
    }

    [Fact]
    public void ReturningToCategoriesTakesTenPercentLessTimeThanOpening()
    {
        var transition = new SongFolderTransition();
        for (var tick = 0; tick < SongFolderTransition.Duration; tick++) transition.Advance(2, true);
        Assert.Equal(1, transition.Progress);
        transition.Close();
        var closingTicks = 0;
        while (transition.Progress > 0 && closingTicks < SongFolderTransition.Duration)
        {
            transition.Advance(-1, false);
            closingTicks++;
        }
        Assert.Equal((int)MathF.Ceiling(SongFolderTransition.Duration * .9f), closingTicks);
        Assert.Equal(0, transition.Progress);
    }

    [Fact]
    public void SongDetailsFadeBeforeTheClosingFrameCanPassThroughThem()
    {
        var transition = new SongFolderTransition();
        for (var tick = 0; tick < SongFolderTransition.Duration; tick++) transition.Advance(2, true);
        transition.Close();
        var previous = 1f;
        for (var tick = 0; tick < SongFolderTransition.Duration; tick++)
        {
            transition.Advance(-1, false);
            foreach (var fraction in new[] { 0f, .25f, .5f, .75f, 1f })
            {
                var alpha = transition.SongContentOpacity(fraction);
                Assert.InRange(alpha, 0, previous);
                if (transition.SongWidth(fraction) < 1) Assert.Equal(0, alpha);
                previous = alpha;
            }
        }
    }

    [Fact]
    public void ClosingTheFolderFreezesAnIncompleteSongRevealWithoutRestartingIt()
    {
        var reveal = new SongPreviewReveal();
        for (var tick = 0; tick < 4; tick++) reveal.Advance(2, 1, true, true);
        var visible = reveal.TitleOpacity(1);
        Assert.InRange(visible, .01f, .99f);
        for (var tick = 0; tick < 60; tick++)
        {
            reveal.Hold();
            foreach (var fraction in new[] { 0f, .5f, 1f }) Assert.Equal(visible, reveal.TitleOpacity(fraction));
        }
        reveal.Advance(2, -1, false, false);
        Assert.Equal(0, reveal.TitleOpacity(1));
    }

    [Fact]
    public void FrameCornersStayTheSameSizeAndSlicesMeetAtEveryHeight()
    {
        foreach (var height in new[] { 89f, 270, 600, 6524 })
        {
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                {
                    var slice = VerticalCategoryFrame.Slice(row, column, height);
                    Assert.True(slice.Width > 0 && slice.Height > 0);
                    var baseline = VerticalCategoryFrame.Slice(row, column, 270);
                    if (row != 1) Assert.Equal(baseline.Width, slice.Width, 2);
                    Assert.Equal(baseline.Height, slice.Height, 2);
                    if (row < 2)
                    {
                        var next = VerticalCategoryFrame.Slice(row + 1, column, height);
                        Assert.Equal(slice.X + slice.Width / 2, next.X - next.Width / 2, 2);
                    }
                    if (column < 2)
                    {
                        var next = VerticalCategoryFrame.Slice(row, column + 1, height);
                        Assert.Equal(slice.Y + slice.Height / 2, next.Y - next.Height / 2, 2);
                    }
                }
            var top = VerticalCategoryFrame.Slice(0, 0, height);
            var bottom = VerticalCategoryFrame.Slice(2, 0, height);
            Assert.Equal(VerticalCategoryFrame.Top, top.X - top.Width / 2, 2);
            Assert.Equal(height, (bottom.X + bottom.Width / 2 - VerticalCategoryFrame.Top) * .65f, 2);
        }
    }

    [Fact]
    public void CategoryLengthIncludesEveryBoardRatherThanAFixedFiveRowWindow()
    {
        Assert.Equal(89, VerticalSongRowsLayout.CategoryHeight(1));
        Assert.Equal(284, VerticalSongRowsLayout.CategoryHeight(4));
        Assert.Equal(6524, VerticalSongRowsLayout.CategoryHeight(100));
        Assert.Equal(0, VerticalSongRowsLayout.ScrollOffset(2));
        Assert.Equal(65, VerticalSongRowsLayout.ScrollOffset(3));
    }

    [Fact]
    public void ExpansionStartsAtTheTopAndRevealsTheBottomWithoutChangingTheRowPitch()
    {
        Assert.Equal(270, VerticalSongRowsLayout.RevealHeight(100, 0));
        Assert.InRange(VerticalSongRowsLayout.RevealHeight(100, .5f), 270, 600);
        Assert.Equal(VerticalSongRowsLayout.CategoryHeight(100), VerticalSongRowsLayout.RevealHeight(100, 1));
        for (var board = 0; board < 100; board++)
            Assert.Equal(100, VerticalSongRowsLayout.RowPosition(board + 1) - VerticalSongRowsLayout.RowPosition(board), 3);
    }

    [Fact]
    public void ReversingAScrollKeepsEveryBoardAtTheSamePitch()
    {
        var motion = new VerticalSongRowsMotion();
        motion.Advance(8, 30);
        foreach (var selected in new[] { 9, 10, 9, 8, 7, 8 })
        {
            motion.Advance(selected, 30);
            for (var board = 4; board < 13; board++)
                Assert.Equal(100, VerticalSongRowsLayout.RowPosition(board + 1)
                    - VerticalSongRowsLayout.RowPosition(board), 3);
        }
        for (var tick = 0; tick < 60; tick++) motion.Advance(8, 30);
        Assert.Equal(8, motion.Cursor);
        Assert.Equal(1, motion.SelectionAlpha);
    }

    [Fact]
    public void WrappingOrJumpingToAnotherWindowNeverScrollsThroughEmptySpace()
    {
        var motion = new VerticalSongRowsMotion();
        motion.Advance(99, 100);
        motion.Advance(0, 100);
        Assert.Equal(0, motion.Cursor);
        motion.Advance(70, 100);
        Assert.Equal(70, motion.Cursor);
    }

    [Fact]
    public void VerticalDWheelBulgesLeftAndOtherRowsRecedeRightWithoutOvershoot()
    {
        Assert.Equal(0, SongSelectCircularPath.VerticalOffset(288));
        var previous = 0f;
        foreach (var distance in new[] { 1f, 32, 73.11f, 146.22f, 234, 350, 480, 900 })
        {
            var offset = SongSelectCircularPath.VerticalOffset(288 + distance);
            Assert.Equal(offset, SongSelectCircularPath.VerticalOffset(288 - distance), 3);
            Assert.InRange(offset, previous, SongSelectCircularPath.Radius);
            previous = offset;
        }
    }

    [Theory]
    [InlineData(.15f)]
    [InlineData(.65f)]
    [InlineData(1f)]
    public void CustomGapKeepsCardSizeAndFocusDuringFolderOpeningAndWrap(float gapScale)
    {
        var browser = new VerticalSongBrowser(gapScale);
        var opened = new[] { false, false, false };
        var counts = new[] { 20, 30, 10 };
        browser.Advance(opened, counts, 1, -1, 246, motion: new(1, 0));
        var categoryPitch = VerticalSongBrowser.CompactRowHeight
            + (VerticalSongBrowser.CategoryPitch - VerticalSongBrowser.CompactRowHeight) * gapScale;
        Assert.Equal(categoryPitch, browser.Height(0), 3);
        opened[1] = true;
        var pitch = VerticalSongBrowser.CompactRowHeight
            + (VerticalSongBrowser.SongRowPitch - VerticalSongBrowser.CompactRowHeight) * gapScale;
        for (var tick = 0; tick < 60; tick++)
        {
            browser.Advance(opened, counts, 1, 1, 246, motion: new(1, Math.Clamp(tick / 40f, 0, 1)));
            Assert.InRange(Math.Abs(pitch + browser.VisibleRowExpansion(0)
                - (browser.VisibleRowTop(1) - browser.VisibleRowTop(0))), 0, .001f);
            Assert.Equal(288, browser.SelectionTop - browser.Camera + 25.35f + (browser.SelectionHeight - 65) / 2, 2);
        }
        browser.Advance(opened, counts, 1, 29, 246, direction: -1, motion: new(1, 1));
        browser.Advance(opened, counts, 1, 0, 246, direction: 1, motion: new(1, 1));
        Assert.Equal(pitch, browser.VisibleRowTop(-1) - browser.VisibleRowTop(-2), 2);
        Assert.Equal(246, browser.SelectionHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    public void FloatingCategoryHeaderUsesTheSameCompactPitchAboveAndBelowIncludingWrap(int genre)
    {
        const int count = 13;
        var browser = new VerticalSongBrowser(.15f, overlayCategoryHeader: true, categoryGapScale: -.25f);
        var opened = new bool[count]; var counts = Enumerable.Repeat(10, count).ToArray();
        browser.Advance(opened, counts, genre, -1, 246, motion: new(1, 0));
        var focus = browser.Top(genre) + browser.BodyHeight(genre) / 2;
        var above = (genre + count - 1) % count; var below = (genre + 1) % count;
        var topDistance = focus - (browser.Top(above) + browser.BodyHeight(above) / 2);
        var bottomDistance = browser.Top(below) + browser.BodyHeight(below) / 2 - focus;
        Assert.InRange(Math.Abs(topDistance - bottomDistance), 0, .001f);
        Assert.Equal(288, focus - browser.Camera, 3);
        Assert.Equal(VerticalSongBrowser.PreviewBodyHeight, browser.BodyHeight(genre));
        Assert.Equal(VerticalSongBrowser.CompactRowHeight, browser.BodyHeight(above));
        var pitch = VerticalSongBrowser.CompactRowHeight
            - (VerticalSongBrowser.CategoryPitch - VerticalSongBrowser.CompactRowHeight) * .25f;
        Assert.Equal(pitch, browser.Height(above), 3);
        // This body can pass behind the floating label instead of reserving its 46px slot.
        Assert.True(browser.Top(above) + browser.BodyHeight(above) > browser.Top(genre) - 46);
    }

    [Fact]
    public void PerspectiveKeepsFocusedCardFullSizeAndChangesContinuouslyWithDistance()
    {
        Assert.Equal(1, SongSelectCircularPath.PerspectiveScale(288));
        var previous = 1f;
        for (var distance = 0; distance <= 1200; distance++)
        {
            var scale = SongSelectCircularPath.PerspectiveScale(288 + distance);
            Assert.Equal(scale, SongSelectCircularPath.PerspectiveScale(288 - distance));
            Assert.InRange(scale, .84f, previous);
            Assert.InRange(previous - scale, 0, .000301f);
            previous = scale;
        }
    }

    [Fact]
    public void CircularPathIsSymmetricAndFollowsACircleWithoutChangingCardSize()
    {
        Assert.Equal(0, SongSelectCircularPath.Inset(150));
        foreach (var distance in new[] { 50f, 100, 250, 400, 600 })
        {
            var inset = SongSelectCircularPath.Inset(150 + distance) * 1.1f;
            Assert.Equal(SongSelectCircularPath.Inset(150 - distance), SongSelectCircularPath.Inset(150 + distance));
            var y = Math.Min(distance * .65f, SongSelectCircularPath.Radius);
            var radius = SongSelectCircularPath.Radius;
            Assert.InRange(Math.Abs((radius - inset) * (radius - inset) + y * y - radius * radius), 0, .1f);
        }
    }

    [Fact]
    public void LongTitlePausesAtBothEndsAndNeverLeavesItsViewport()
    {
        Assert.Equal(0, SongTitleMarquee.Offset(1000, 399));
        Assert.Equal(0, SongTitleMarquee.Offset(90, 600));
        Assert.Equal(50, SongTitleMarquee.Offset(140, 600));
        Assert.Equal(200, SongTitleMarquee.Offset(310, 600));
        Assert.Equal(150, SongTitleMarquee.Offset(400, 600));
        for (var age = 0; age < 2000; age++) Assert.InRange(SongTitleMarquee.Offset(age, 600), 0, 200);
    }
}
