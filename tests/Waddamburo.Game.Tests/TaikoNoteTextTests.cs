using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoNoteTextTests
{
    [Fact]
    public void TjaRunsGetTheirSyllables()
    {
        // 120 BPM: an 8th is 250 ms, a 16th 125 ms.
        var d = PlayableNoteKind.Don;
        var k = PlayableNoteKind.Ka;
        var chart = create(
            (0, k),                                  // isolated: katsu
            (2000, d), (2250, d), (2500, d),         // 8ths, odd all-don run: do ko don
            (4000, d), (4125, k), (4250, d), (4375, d), // fast run of four: stays short
            (6000, PlayableNoteKind.BigDon), (6250, d)); // big notes keep their text; last note full
        Assert.Equal(["katsu", "do", "ko", "don", "do", "ka", "do", "do", "don_dai", "don"], TaikoNoteText.Labels(chart));
    }

    [Fact]
    public void AuthoredFumenTypesWin()
    {
        var chart = create((0, PlayableNoteKind.Don), (125, PlayableNoteKind.Don), (250, PlayableNoteKind.Ka));
        var authored = new PlayableChart(chart.Key, chart.AuthoredOffset, chart.Duration,
            chart.HitObjects.Select((note, index) => note with { InRun = index < 2, IsKo = index == 1 }),
            chart.TimingPoints, chart.ScrollPoints, chart.EffectPoints, chart.BarLines);
        Assert.Equal(["do", "ko", "katsu"], TaikoNoteText.Labels(authored));
    }

    private static PlayableChart create(params (int Ms, PlayableNoteKind Kind)[] notes) =>
        new(new ChartKey(new SongKey(SongSourceKind.Tja, "synthetic"), "text"), TimeSpan.Zero, TimeSpan.FromSeconds(10),
            notes.Select(note => new PlayableHitObject(TimeSpan.FromMilliseconds(note.Ms), note.Kind)),
            [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
            [new ChartScrollPoint(TimeSpan.Zero, 1)], [new ChartEffectPoint(TimeSpan.Zero, false)],
            [new ChartBarLine(TimeSpan.Zero, true)]);
}
