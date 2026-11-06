using Waddamburo.Catalog;
using Waddamburo.Game.Online;

namespace Waddamburo.Game.Tests;

public sealed class SongPackageTests
{
    private static readonly SongKey Song = new(SongSourceKind.Tja, "host-song");

    private static PlayableChart chart(TaikoCourse course) => new(
        new ChartKey(Song, $"chart-{course}"),
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromSeconds(95),
        Enumerable.Range(0, 40).Select(static index => new PlayableHitObject(TimeSpan.FromSeconds(index + 1), index % 2 == 0 ? PlayableNoteKind.Don : PlayableNoteKind.Ka)),
        [new ChartTimingPoint(TimeSpan.Zero, 150, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)],
        [new PlayableLongNote(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(52), PlayableLongNoteKind.Kusudama, 30)])
    { Level = 8 };

    private static SongPackage package() => new(Song, new SongTitle("Song", "曲"), "Artist", "Sub", TimeSpan.FromSeconds(30),
        [
            new PackagedChart("chart-oni", TaikoCourse.Oni, 8, "Oni", chart(TaikoCourse.Oni)),
            new PackagedChart("chart-easy", TaikoCourse.Easy, 2, "Easy", chart(TaikoCourse.Easy)),
        ], [1, 2, 3, 4, 5]);

    [Fact]
    public void ASentSongComesBackWithTheHostsKeysLengthAndAudio()
    {
        var read = SongPackage.Decode(package().Encode(), Song);

        Assert.NotNull(read);
        Assert.Equal(("Song", "曲", "Artist", "Sub", TimeSpan.FromSeconds(30)), (read.Title.Primary, read.Title.Japanese, read.Artist, read.Subtitle, read.PreviewStart));
        Assert.Equal([TaikoCourse.Oni, TaikoCourse.Easy], read.Charts.Select(static entry => entry.Course));
        var oni = read.Charts[0];
        Assert.Equal((new ChartKey(Song, "chart-oni"), TimeSpan.FromSeconds(95), 40, 8), (oni.Chart.Key, oni.Chart.Duration, oni.Chart.NoteCount, oni.Level));
        Assert.Equal(PlayableLongNoteKind.Kusudama, oni.Chart.LongNotes[0].Kind);
        Assert.Equal(ChartHash.Compute(chart(TaikoCourse.Oni), TaikoCourse.Oni), ChartHash.Compute(oni.Chart, TaikoCourse.Oni));
        Assert.Equal([1, 2, 3, 4, 5], read.Audio);
    }

    [Fact]
    public void AGimmickChartArrivesWithWhatItDraws()
    {
        // Drawing-only data (not in the canonical bytes): complex scroll, #SUDDEN, beat scrolling, target moves, labels.
        var gimmick = chart(TaikoCourse.Oni) with
        {
            ScrollPoints = [new ChartScrollPoint(TimeSpan.Zero, 1),
                new ChartScrollPoint(TimeSpan.FromSeconds(2), -1) { Imaginary = 2, Show = TimeSpan.FromSeconds(1), Wait = TimeSpan.FromSeconds(0.5), ByBeat = true }],
            JudgeMoves = [new ChartJudgeMove(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 390, -100)],
            Beats = [new ChartBeatPoint(TimeSpan.Zero, 0, 2.5), new ChartBeatPoint(TimeSpan.FromSeconds(4), 10, 0)],
        };
        gimmick = gimmick with { HitObjects = gimmick.HitObjects.SetItem(0, gimmick.HitObjects[0] with { Text = "ko" }) };
        var sent = new SongPackage(Song, new SongTitle("Song"), null, null, null, [new PackagedChart("chart-oni", TaikoCourse.Oni, 8, "Oni", gimmick)], [1]);

        var read = Assert.Single(SongPackage.Decode(sent.Encode(), Song)!.Charts).Chart;

        Assert.Equal(gimmick.ScrollPoints.AsEnumerable(), read.ScrollPoints.AsEnumerable());
        Assert.Equal(gimmick.JudgeMoves.AsEnumerable(), read.JudgeMoves.AsEnumerable());
        Assert.Equal(gimmick.Beats.AsEnumerable(), read.Beats.AsEnumerable());
        Assert.Equal("ko", read.HitObjects[0].Text);
        Assert.Equal(ChartHash.Compute(gimmick, TaikoCourse.Oni), ChartHash.Compute(read, TaikoCourse.Oni));
    }

    [Fact]
    public void AnotherSongOrBrokenBytesAreRefused()
    {
        var bytes = package().Encode();

        Assert.Null(SongPackage.Decode(bytes, new SongKey(SongSourceKind.Tja, "other")));
        Assert.Null(SongPackage.Decode(bytes, new SongKey(SongSourceKind.OsuLazer, "host-song")));
        Assert.Null(SongPackage.Decode(bytes[..^1], Song));
        Assert.Null(SongPackage.Decode([.. bytes, 0], Song));
        var corrupt = bytes.ToArray();
        corrupt[^20] ^= 0xFF;
        corrupt[200] ^= 0xFF;
        _ = SongPackage.Decode(corrupt, Song); // never throws
        Assert.Null(SongPackage.Decode([.. "WDBS"u8, 2, 0xFF, 0xFF, 0xFF, 0x7F], Song));
    }
}
