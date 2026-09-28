using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The end-of-song banner (action_result labels). Traced in the original game on four songs from
/// 120 to 212 BPM: the host jumps the movie to fail / success / fullcombo 1.3 s after the chart's last
/// note or roll ends (1309-1347 ms, whatever the tempo), not at the audio's end. The shutter then
/// closes <see cref="ShutterDelay"/> later.
/// </summary>
public static class TaikoResultBanner
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1.3);

    /// <summary>Banner to shutter close: 9.0 s in both traced songs, while the song's audio had already ended.</summary>
    public static readonly TimeSpan ShutterDelay = TimeSpan.FromSeconds(9);

    public static TimeSpan Time(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return chart.HitObjects.Select(note => note.StartTime)
            .Concat(chart.LongNotes.Select(note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max() + Delay;
    }

    public static string Label(bool cleared, bool fullCombo) =>
        !cleared ? "fail" : fullCombo ? "fullcombo" : "success";
}
