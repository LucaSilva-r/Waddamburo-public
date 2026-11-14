namespace Waddamburo.Catalog;

/// <summary>
/// A song's browsing tempo and length (<see cref="SongDescriptor.Bpm"/>, <see cref="SongDescriptor.Length"/>) taken
/// from one of its charts, for sources that do not state them.
/// </summary>
public sealed record SongTempo(double? Bpm, TimeSpan? Length)
{
    /// <summary>
    /// Main tempo = the BPM held longest up to the last note; length = from the start to the last note's end.
    /// </summary>
    public static SongTempo Of(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var end = TimeSpan.Zero;
        foreach (var note in chart.HitObjects)
            end = note.StartTime > end ? note.StartTime : end;
        foreach (var note in chart.LongNotes)
            end = note.EndTime > end ? note.EndTime : end;
        var held = new Dictionary<double, TimeSpan>();
        var timing = chart.TimingPoints; // ordered, from time zero
        for (var index = 0; index < timing.Length; index++)
        {
            var from = timing[index].Time;
            var to = index + 1 < timing.Length ? timing[index + 1].Time : end;
            var bpm = Math.Round(timing[index].BeatsPerMinute, 2);
            held[bpm] = held.GetValueOrDefault(bpm) + (to > from ? to - from : TimeSpan.Zero);
        }
        double? main = held.Count == 0 ? null : held.MaxBy(static pair => pair.Value).Key;
        return new SongTempo(main > 0 ? main : null, end > TimeSpan.Zero ? end : null);
    }
}
