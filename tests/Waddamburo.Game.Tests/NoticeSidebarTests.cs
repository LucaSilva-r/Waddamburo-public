using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class NoticeSidebarTests
{
    [Fact]
    public void WrapsAtSpacesCountsWideCharactersDoubleAndCutsLongText()
    {
        Assert.Equal(["Server maintenance", "at 22:00 tonight"], TextWrap.Lines("Server maintenance at 22:00 tonight", 18, 6));
        // No spaces (Japanese): broken by width, a wide character counting about two letters.
        Assert.Equal(["メンテナンス", "を行います"], TextWrap.Lines("メンテナンスを行います", 11, 6));
        Assert.Equal(["first", "second"], TextWrap.Lines("first\nsecond", 40, 6));
        var cut = TextWrap.Lines(string.Join(' ', Enumerable.Repeat("word", 40)), 10, 3);
        Assert.Equal(3, cut.Count);
        Assert.EndsWith("…", cut[^1], StringComparison.Ordinal);
        Assert.All(cut, line => Assert.DoesNotMatch("^ | $", line));
    }

    [Fact]
    public void JobsShowOnlyOnceTheyTakeAWhileAndLeaveAfterEnding()
    {
        var clock = new ManualClock();
        var board = new JobBoard(clock);
        var quick = board.Start("quick");
        var slow = board.Start("slow", total: 4);
        Assert.Empty(board.Visible);

        quick.Complete(); // done before it would have shown: never flashes up
        clock.Advance(JobBoard.ShowAfter);
        slow.Report(1);
        Assert.Equal("slow", Assert.Single(board.Visible).Title);
        Assert.Equal(0.25, slow.Fraction);

        slow.Fail("offline");
        clock.Advance(JobBoard.KeepFinished);
        Assert.Equal("offline", Assert.Single(board.Visible).Failure);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(board.Visible);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
