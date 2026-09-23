using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>One player's finished play, as the results screen shows it.</summary>
public sealed record TaikoPlayResult(
    TaikoCourse Course,
    long Score,
    int Great,
    int Good,
    int Miss,
    int MaxCombo,
    int Rolls,
    int GaugeSegments,
    bool Cleared)
{
    public bool FullCombo => Cleared && Miss == 0;

    /// <summary>
    /// result.lm SetResultLevel: RESULT_FAILURE 0 / ALMOST 1 / SUCCESS 2 / FULL 3 (traced: a full gauge
    /// with 3 misses sent 3). ponytail: ALMOST never sent, its threshold is unknown.
    /// </summary>
    public int ResultLevel => !Cleared ? 0 : GaugeSegments >= TaikoSoulGauge.Segments ? 3 : 2;

    /// <summary>result.lm SetCourse: 0 easy .. 3 oni, 4 ura (traced: oni 3, normal 1).</summary>
    public int CourseIndex => Course switch
    {
        TaikoCourse.Easy => 0,
        TaikoCourse.Normal => 1,
        TaikoCourse.Hard => 2,
        TaikoCourse.Oni => 3,
        _ => 4,
    };
}
