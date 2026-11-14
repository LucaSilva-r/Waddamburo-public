using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Skins.Green.Gameplay;

namespace Waddamburo.Skins.Green.Tests;

public sealed class TaikoSkinPresentationTests
{
    private static TaikoNoteJudgement judgement(TaikoHitResult result) =>
        new(0, new(TimeSpan.Zero, PlayableNoteKind.Don), result, TimeSpan.Zero, false);

    [Fact]
    public void DancersJoinPerQuarterOfTheClearLine()
    {
        // Hard (clear 7000): one dancer, then one more every 1750 points (trace-confirmed).
        var gauge = new TaikoSoulGauge(TaikoCourse.Hard, 5, 100);
        var counts = new List<int> { TaikoSkinPresentation.DancerCount(gauge) };
        while (gauge.State != TaikoGaugeState.Full)
        {
            gauge.Apply(judgement(TaikoHitResult.Great));
            counts.Add(TaikoSkinPresentation.DancerCount(gauge));
        }
        Assert.Equal([1, 2, 3, 4, 5, 6], counts.Distinct());
        Assert.Equal(1 + TaikoSoulGauge.Max * 4 / 7000, TaikoSkinPresentation.DancerCount(gauge));
    }
}
