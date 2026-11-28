using System.Collections.Concurrent;
using Waddamburo.Catalog;

namespace Waddamburo.App.Audio;

/// <summary>
/// Where the music playing now is in its beat (music.beat, music.bpm for movies): a song's beat map comes from one of
/// its charts, loaded in the background the first time the song plays (a preview), and kept.
/// </summary>
internal sealed class MusicBeats(Func<SongKey, PlayableChart?> loadChart)
{
    // ponytail: kept for every song played this session (a few KB each); evict if a session ever plays thousands.
    private readonly ConcurrentDictionary<SongKey, Task<Map?>> _maps = new();

    private sealed record Map(ChartEffects Beats, TimeSpan Offset);

    /// <summary>Starts loading the song's beat map (once).</summary>
    public void Want(SongKey song) => _maps.GetOrAdd(song, key => Task.Run(() => load(key)));

    private Map? load(SongKey song)
    {
        try
        {
            if (loadChart(song) is not { } chart)
                return null;
            var beats = chart.Effects.Beats.IsEmpty ? new ChartEffects { Beats = ChartModFile.BeatsOf(chart.TimingPoints) } : chart.Effects;
            return beats.Beats.IsEmpty ? null : new Map(beats, chart.AuthoredOffset);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"No beat for {song}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// The song's beat count at <paramref name="audio"/> into its audio, and its tempo there; null until its beat map is
    /// loaded (or when it has none). Before the chart's first beat the count is negative.
    /// </summary>
    public (double Beat, double Bpm)? At(SongKey song, TimeSpan audio)
    {
        if (!_maps.TryGetValue(song, out var task) || !task.IsCompletedSuccessfully || task.Result is not { } map)
            return null;
        // A chart's time is its audio's plus its offset (GameplayTimeline: a TJA's OFFSET puts chart zero before audio zero).
        var time = audio + map.Offset;
        var beat = map.Beats.BeatAt(time);
        var bpm = (map.Beats.BeatAt(time + TimeSpan.FromMilliseconds(10)) - beat) * 6000;
        return (beat, Math.Round(bpm, 2));
    }
}
