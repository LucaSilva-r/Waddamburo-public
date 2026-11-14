using Waddamburo.Catalog;

namespace Waddamburo.Catalog.Tests;

public sealed class SongTempoTests
{
    [Fact]
    public void MainTempoIsTheLongestHeldAndLengthEndsAtTheLastNote()
    {
        static TimeSpan s(double seconds) => TimeSpan.FromSeconds(seconds);
        var chart = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Stock, "song"), "oni"), TimeSpan.Zero, s(200),
            [new PlayableHitObject(s(1), PlayableNoteKind.Don), new PlayableHitObject(s(90), PlayableNoteKind.Ka)],
            [new ChartTimingPoint(s(0), 120, 4, 4), new ChartTimingPoint(s(10), 200, 4, 4), new ChartTimingPoint(s(80), 120, 4, 4)],
            [new(s(0), 1)], [new(s(0), false)], [], [new PlayableLongNote(s(95), s(130), PlayableLongNoteKind.Roll)]);

        var tempo = SongTempo.Of(chart);

        Assert.Equal(200, tempo.Bpm); // 70 s at 200 against 60 s at 120
        Assert.Equal(s(130), tempo.Length);
    }
}
