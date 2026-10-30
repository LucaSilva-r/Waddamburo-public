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
    public void ThePanelShowsWhatRedAsksFor()
    {
        var exams = new DanExams(course(exam("hits", 100, 0), exam("score", 0, 5000)));

        Assert.Equal("hits", Assert.Single(exams.Requirements).Type);
    }
}
