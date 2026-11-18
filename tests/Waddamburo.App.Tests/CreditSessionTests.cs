using Waddamburo.App.Flow.Credit;

namespace Waddamburo.App.Tests;

public sealed class CreditSessionTests
{
    [Fact]
    public void ModeFlagsNeverBothHold()
    {
        var credit = new CreditSession { Waiwai = true };
        Assert.Equal(CreditMode.Waiwai, credit.Mode);
        credit.AiBattle = true;
        Assert.True(credit.AiBattle);
        Assert.False(credit.Waiwai);
        // Clearing the flag that does not hold leaves the mode alone.
        credit.Waiwai = false;
        Assert.Equal(CreditMode.AiBattle, credit.Mode);
        credit.AiBattle = false;
        Assert.Equal(CreditMode.Normal, credit.Mode);
    }

    [Fact]
    public void ANewCreditOffersTheTutorialsAgain()
    {
        var credit = new CreditSession { TutorialSeen = true, WaiwaiTutorialSeen = true, AiTutorialSeen = true, SongsPlayed = 2 };
        credit.SongsPlayed = 3;
        Assert.True(credit.TutorialSeen && credit.WaiwaiTutorialSeen && credit.AiTutorialSeen);
        credit.SongsPlayed = 0;
        Assert.False(credit.TutorialSeen || credit.WaiwaiTutorialSeen || credit.AiTutorialSeen);
    }
}
