using Waddamburo.Catalog;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Deals a beatmap set's taiko difficulties into song entries. osu! difficulties follow no Taiko
/// course scheme (one chart or eleven, named Kantan…Inner Oni, "Lv.300" or a mapper's joke), so the
/// courses are only slots: the charts in star order fill Easy, Normal, Hard, Oni, four per entry. Ura
/// is never used (the song select only reveals it by pressing right on Oni). Every chart plays with
/// Oni's judgement.
/// </summary>
public static class OsuCourseLayout
{
    private static readonly TaikoCourse[] Slots = [TaikoCourse.Easy, TaikoCourse.Normal, TaikoCourse.Hard, TaikoCourse.Oni];

    /// <summary>The star level shown (1-10): osu!'s own star rating, rounded.</summary>
    public static int Level(double stars) =>
        double.IsFinite(stars) && stars >= 0 ? (int)Math.Clamp(Math.Round(stars, MidpointRounding.AwayFromZero), 1, 10) : 1;

    /// <summary>The entries a set's charts make, each a course → chart map.</summary>
    public static List<Dictionary<TaikoCourse, T>> Deal<T>(IEnumerable<T> charts, Func<T, string> name, Func<T, double> stars) =>
        [.. charts.OrderBy(stars).ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .Chunk(Slots.Length)
            .Select(static entry => entry.Select(static (chart, index) => (chart, index)).ToDictionary(
                static pair => Slots[pair.index], static pair => pair.chart))];
}
