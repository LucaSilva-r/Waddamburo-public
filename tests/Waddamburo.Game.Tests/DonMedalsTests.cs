using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class DonMedalsTests
{
    [Fact]
    public void ACreditPaysGreensBonusesByTheLastPaidDay()
    {
        var thursday = new DateOnly(2026, 11, 19);
        Assert.Equal(new MedalCredit(550, true, true, true), DonMedals.ForCredit(null, thursday));
        Assert.Equal(new MedalCredit(100, false, false, false), DonMedals.ForCredit(thursday, thursday));
        Assert.Equal(new MedalCredit(150, true, false, false), DonMedals.ForCredit(thursday, thursday.AddDays(1)));
        Assert.Equal(new MedalCredit(250, true, true, false), DonMedals.ForCredit(thursday, new DateOnly(2026, 11, 23))); // Monday
        Assert.Equal(new MedalCredit(550, true, true, true), DonMedals.ForCredit(thursday, new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void TheSeasonFollowsGreensWindows()
    {
        Assert.Equal(3, DonMedals.Season(new DateOnly(2026, 11, 19)).Id);
        Assert.Equal(4, DonMedals.Season(new DateOnly(2026, 12, 5)).Id);
        Assert.Equal(4, DonMedals.Season(new DateOnly(2027, 3, 13)).Id);
        Assert.Equal(1, DonMedals.Season(new DateOnly(2027, 3, 14)).Id);
        Assert.Equal("Summer reward shop", DonMedals.Season(new DateOnly(2027, 5, 30)).Telop);
    }
}
