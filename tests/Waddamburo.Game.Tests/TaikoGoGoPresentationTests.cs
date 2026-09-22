using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoGoGoPresentationTests
{
    [Fact]
    public void RepeatedUpdatesAndRedundantChartMarkersDoNotRestartGoGo()
    {
        var transitions = new List<bool>();
        var presentation = new TaikoGoGoPresentation([
            new ChartEffectPoint(TimeSpan.Zero, true),
            new ChartEffectPoint(TimeSpan.FromSeconds(2), true),
            new ChartEffectPoint(TimeSpan.FromSeconds(5), false)], transitions.Add);
        presentation.Update(TimeSpan.FromSeconds(-1));
        for (var tick = 0; tick <= 360; tick++)
        {
            var time = TimeSpan.FromSeconds(tick / 60d);
            presentation.Update(time);
            presentation.Update(time);
        }
        Assert.Equal([false, true, false], transitions);
    }
}
