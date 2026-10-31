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
