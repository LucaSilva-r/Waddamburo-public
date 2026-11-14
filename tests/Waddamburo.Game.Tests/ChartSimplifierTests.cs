using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class ChartSimplifierTests
{
    // 120 BPM: a beat is 500 ms, a 16分 125 ms, an 8分 250 ms.
    private static PlayableChart chart(params (double Ms, PlayableNoteKind Kind)[] notes) => new(
        new ChartKey(new SongKey(SongSourceKind.Stock, "a"), "m"), TimeSpan.Zero, TimeSpan.FromSeconds(30),
        [.. notes.Select(static note => new PlayableHitObject(TimeSpan.FromMilliseconds(note.Ms), note.Kind))],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)]);

    private static double[] times(PlayableChart chart) => [.. chart.HitObjects.Select(static note => note.StartTime.TotalMilliseconds)];

    [Fact]
    public void ASixteenthStreamIsSplitThenThinnedDownTheOniBand()
    {
        // Seven 16分 on the beat, then a lone note.
        var oni = chart([.. Enumerable.Range(0, 7).Select(static i => (1000 + i * 125.0, PlayableNoteKind.Don)), (3000, PlayableNoteKind.Ka)]);
        Assert.Equal(oni, ChartSimplifier.Simplify(oni, 13));
        // 12: 3-1-1 (three, then every other note).
        Assert.Equal([1000, 1125, 1250, 1500, 1750, 3000], times(ChartSimplifier.Simplify(oni, 12)));
        // 11 adds: the on-beat 16分 triplet left becomes 8分.
        Assert.Equal([1000, 1250, 1500, 1750, 3000], times(ChartSimplifier.Simplify(oni, 11)));
    }

    [Fact]
    public void MixedEighthTripletsTurnOneColourInTheNormalBand()
    {
        var normal = chart((1000, PlayableNoteKind.Don), (1250, PlayableNoteKind.Ka), (1500, PlayableNoteKind.Don));
        var level3 = ChartSimplifier.Simplify(normal, 3);
        Assert.Equal([1000, 1250, 1500], times(level3));
        Assert.All(level3.HitObjects, static note => Assert.Equal(PlayableNoteKind.Don, note.Kind));
        Assert.Equal(TaikoCourse.Normal, ChartSimplifier.SourceCourse(3));
        Assert.Null(ChartSimplifier.SourceCourse(9));
    }

    [Fact]
    public void SupportInfoCountsEachCoursesRecipes()
    {
        static string row(params int[] ids) => $"<item><elems><count>3</count>{string.Concat(ids.Select(static id => $"<item>{id}</item>"))}</elems></item>";
        var xml = $"<boost_serialization><musicid>uhear2</musicid><supportinfo><coursepatterns><elems><count>5</count>"
            + row(0, 0, 0) + row(13, 14, 15) + row(7, 8, 9) + row(1, 2, 0) + row(0, 0, 0) + "</elems></coursepatterns></supportinfo></boost_serialization>";
        Assert.Equal([0, 3, 3, 2], SupportInfo.Parse(xml)["uhear2"]);
        // The menu's list per count (おに 13): 1 -> 11; 2 -> 11, 10; 3 -> 12, 11, 10.
        Assert.Equal(11, SupportInfo.SupportPick(13, 1, 1));
        Assert.Equal([11, 10], Enumerable.Range(1, 2).Select(static pick => SupportInfo.SupportPick(13, 2, pick)));
        Assert.Equal([12, 11, 10], Enumerable.Range(1, 3).Select(static pick => SupportInfo.SupportPick(13, 3, pick)));
        Assert.Equal(13, SupportInfo.SupportPick(13, 3, 0));
    }
}
