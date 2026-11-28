using Waddamburo.App.Audio;
using Waddamburo.Catalog;

namespace Waddamburo.App.Tests;

public sealed class MusicBeatsTests
{
    private static readonly SongKey Song = new(SongSourceKind.Stock, "song");

    private static PlayableChart chart(TimeSpan offset) => new(new ChartKey(Song, "oni"), offset, TimeSpan.FromSeconds(60), [],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(10), 240, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)], [new ChartEffectPoint(TimeSpan.Zero, false)], [new ChartBarLine(TimeSpan.Zero, true)]);

    private static (double Beat, double Bpm) at(MusicBeats beats, double seconds)
    {
        beats.Want(Song);
        for (var wait = 0; wait < 200 && beats.At(Song, TimeSpan.Zero) is null; wait++)
            Thread.Sleep(5);
        return beats.At(Song, TimeSpan.FromSeconds(seconds))!.Value;
    }

    [Fact]
    public void TheBeatFollowsTheChartsTempoAndOffset()
    {
        // 120 BPM: two beats a second; from 10 s, 240 BPM.
        var plain = new MusicBeats(_ => chart(TimeSpan.Zero));
        Assert.Equal((3, 120), at(plain, 1.5));
        Assert.Equal((24, 240), at(plain, 11));
        // A chart whose time is half a second ahead of its audio (offset): the audio's 1 s is the chart's 1.5 s.
        var offset = new MusicBeats(_ => chart(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(3, at(offset, 1).Beat, 6);
        // No chart: no beat.
        var none = new MusicBeats(_ => null);
        none.Want(Song);
        Thread.Sleep(50);
        Assert.Null(none.At(Song, TimeSpan.Zero));
    }
}
