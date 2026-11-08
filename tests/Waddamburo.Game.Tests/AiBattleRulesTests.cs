using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class AiBattleRulesTests
{
    // Bar lines every 2 s; notes every second up to lastNote.
    private static PlayableChart chart(int lastNote) => new(
        new ChartKey(new SongKey(SongSourceKind.Stock, "a"), "m"), TimeSpan.Zero, TimeSpan.FromSeconds(lastNote + 10),
        [.. Enumerable.Range(1, lastNote).Select(second => new PlayableHitObject(TimeSpan.FromSeconds(second), PlayableNoteKind.Don))],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [.. Enumerable.Range(0, lastNote / 2 + 5).Select(bar => new ChartBarLine(TimeSpan.FromSeconds(bar * 2), true))]);

    [Fact]
    public void ALongSongHasFiveSectionsOnBarLines()
    {
        var ends = AiBattleRules.SectionEnds(chart(101));
        Assert.Equal([20, 40, 60, 80], ends[..4].Select(end => end.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(101.5), ends[4]);
    }

    [Fact]
    public void AShortSongHasThree() => Assert.Equal(3, AiBattleRules.SectionEnds(chart(45)).Length);

    [Fact]
    public void TerritoryWeighsGreatsAndGoods()
    {
        Assert.Equal(3 * 59 + 2 * 6, AiBattleRules.Territory(TaikoCourse.Hard, 59, 6)); // wiki: 189
        Assert.Equal(254, AiBattleRules.Territory(TaikoCourse.Oni, 81, 11)); // wiki
    }
}
