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
        Assert.Null(SongPackage.Decode([.. "WDBS"u8, 1, 0xFF, 0xFF, 0xFF, 0x7F], Song));
    }
}
