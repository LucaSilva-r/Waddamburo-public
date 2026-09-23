using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The end-of-song banner (action_result labels). Traced in the original game: the host jumps the
/// movie to fail / success / fullcombo about one second after the start of the measure holding the
/// chart's last note, whatever the last note's position in it.
/// </summary>
public static class TaikoResultBanner
{
    // ponytail: one traced song (1.03 s ± the trace's ~20 ms anchor); refine with more songs
    // (half a measure would fit it too).
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    public static TimeSpan Time(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var last = chart.HitObjects.Select(note => note.StartTime)
            .Concat(chart.LongNotes.Select(note => note.StartTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max();
        var measure = chart.BarLines.Select(bar => bar.Time).Where(time => time <= last)
            .DefaultIfEmpty(last).Max();
        return measure + Delay;
    }

    public static string Label(bool cleared, bool fullCombo) =>
        !cleared ? "fail" : fullCombo ? "fullcombo" : "success";
}
