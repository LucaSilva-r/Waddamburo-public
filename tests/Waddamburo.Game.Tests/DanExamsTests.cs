using Waddamburo.Game.Dan;

namespace Waddamburo.Game.Tests;

public sealed class DanExamsTests
{
    private static DanCourse course(params DanRequirement[] requirements) =>
        new(1, "Test", "hash", [.. requirements], [new("a", 3, null, 60, []), new("b", 3, null, 40, [])]);

    private static DanRequirement exam(string type, int red, int gold) => new(type, [red], [gold]);

    [Fact]
    public void HitsCountNotesAndRolls()
    {
        Assert.Equal(15, new DanProgress(0, 5, 3, 1, 0, 7, 0).Hits);
    }

    [Fact]
    public void BadsFailTheMomentTheyReachTheLimit()
    {
        var limit = exam("bads", 3, 1);
        var exams = new DanExams(course(limit));

        Assert.True(exams.StillPossible(limit, new DanProgress(0, 0, 0, 2, 0, 0, 0)));
        Assert.False(exams.StillPossible(limit, new DanProgress(0, 0, 0, 3, 0, 0, 0)));
    }

    [Fact]
    public void GoodsFailOnlyWhenTheNotesLeftCannotReachThem()
    {
        var goods = exam("goods", 90, 95);
        var exams = new DanExams(course(goods));

        Assert.True(exams.StillPossible(goods, new DanProgress(0, 0, 0, 10, 0, 0, 0)));
        Assert.False(exams.StillPossible(goods, new DanProgress(0, 0, 0, 11, 0, 0, 0)));
    }

    [Fact]
    public void GaugeIsOnlyJudgedAtTheEnd()
    {
        var gauge = exam("soul_gauge", 90, 95);
        var exams = new DanExams(course(gauge));

        Assert.True(exams.StillPossible(gauge, new DanProgress(0, 0, 0, 50, 0, 0, 0)));
        Assert.False(DanExams.Passes(course(gauge), new DanProgress(89, 0, 0, 0, 0, 0, 0), gold: false));
        Assert.True(DanExams.Passes(course(gauge), new DanProgress(90, 0, 0, 0, 0, 0, 0), gold: false));
    }

    [Fact]
    public void GoldCanAskForSomethingRedDoesNot()
    {
        var test = course(exam("hits", 100, 0), exam("score", 0, 5000));
        var enough = new DanProgress(0, 100, 0, 0, 0, 0, 4000);

        Assert.True(DanExams.Passes(test, enough, gold: false));
        Assert.False(DanExams.Passes(test, enough, gold: true));
        Assert.True(DanExams.Passes(test, enough with { Score = 5000 }, gold: true));
    }

    [Fact]
    public void ResultLevelIsFailRedOrGold()
    {
        var test = course(exam("hits", 100, 200));

        Assert.Equal(0, DanCourseResult.LevelOf(test, new DanProgress(0, 99, 0, 0, 0, 0, 0)));
        Assert.Equal(1, DanCourseResult.LevelOf(test, new DanProgress(0, 100, 0, 0, 0, 0, 0)));
        Assert.Equal(2, DanCourseResult.LevelOf(test, new DanProgress(0, 200, 0, 0, 0, 0, 0)));
    }

    [Theory]
    [InlineData("初級", 0)]
    [InlineData("一級", 10)]
    [InlineData("１級", 10)]
    [InlineData("初段", 11)]
    [InlineData("大将", 15)]
    [InlineData("達人", 24)]
    [InlineData("復活！カツドン大将", DanRanks.Challenge)]
    public void EverySeriesRanksGoOnGreensBoards(string name, int rank)
    {
        Assert.Equal(rank, DanRanks.Of(name));
    }

    [Fact]
    public void APerSongRequirementCountsOnEachSongAlone()
    {
        var rolls = new DanRequirement("rolls", [10, 20, 30], [15, 25, 35]);
        var test = course(rolls);
        var first = new DanProgress(0, 0, 0, 0, 0, 12, 0);
        var second = first with { Rolls = 12 + 20 };

        Assert.False(new DanExams(test).MissedOnSong(first, 0, default));
        Assert.True(new DanExams(test).MissedOnSong(first with { Rolls = 9 }, 0, default));
        Assert.Equal(20, DanExams.ValueOf(rolls, second, first));
        Assert.Equal(1, DanCourseResult.LevelOf(test, second with { Rolls = 62 }, [first, second]));
        Assert.Equal(0, DanCourseResult.LevelOf(test, second with { Rolls = 61 }, [first, second]));
    }

    [Fact]
    public void APerSongLimitFailsOnItsSongAndStartsOverOnTheNext()
    {
        var oks = new DanRequirement("oks", [5, 8, 8], [1, 1, 1]);
        var exams = new DanExams(course(oks));
        var first = new DanProgress(0, 0, 4, 0, 0, 0, 0);

        Assert.True(exams.StillPossible(oks, first, 0));
        Assert.False(exams.StillPossible(oks, first with { Oks = 5 }, 0));
        Assert.True(exams.StillPossible(oks, first with { Oks = 11 }, 1, first));
    }

    [Fact]
    public void FourRequirementsLeaveTheGaugeToItsBar()
    {
        var exams = new DanExams(course(exam("soul_gauge", 94, 97), exam("goods", 500, 600), exam("bads", 55, 44), exam("rolls", 100, 120)));

        Assert.Equal(DanExams.Slots, exams.OnPanel.Count);
        Assert.DoesNotContain(exams.OnPanel, static requirement => requirement.Type == "soul_gauge");
    }

    [Fact]
    public void TheResultsPutWhatWasMissedFirst()
    {
        var exams = new DanExams(course(exam("soul_gauge", 94, 97), exam("goods", 500, 600), exam("bads", 55, 44), exam("rolls", 100, 120)));

        var shown = exams.OnResults(new DanProgress(50, 600, 0, 0, 0, 200, 0), []);

        Assert.Equal(DanExams.Slots, shown.Count);
        Assert.Equal("soul_gauge", shown[0].Type);
    }

    [Fact]
    public void LimitsComeAfterWhatMustBeReached()
    {
        var exams = new DanExams(course(exam("soul_gauge", 94, 97), exam("bads", 55, 44), exam("hits", 610, 640)));

        Assert.Equal(["soul_gauge", "hits", "bads"], exams.Requirements.Select(static requirement => requirement.Type));
    }

    [Fact]
    public void ThePanelShowsWhatRedAsksFor()
    {
        var exams = new DanExams(course(exam("hits", 100, 0), exam("score", 0, 5000)));

        Assert.Equal("hits", Assert.Single(exams.Requirements).Type);
    }
}
