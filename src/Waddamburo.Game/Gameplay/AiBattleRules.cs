using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// AI battle's sections and territory (taiko-fumen wiki, Green's AI battle page; traced session-ai-battle-3): the play
/// is cut into 5 sections (3 for a short song) on bar lines; in each, a side's territory is 良 × A + 可 × B (不可 and
/// rolls count nothing), and the player takes a section when theirs is at least AI-don's.
/// </summary>
public static class AiBattleRules
{
    // ponytail: "short" is a guess (the wiki: by the time from the song's start to the end of play); tune from plays.
    public static readonly TimeSpan ShortSong = TimeSpan.FromSeconds(60);

    // The last section closes this long after the last note (its late hits still count).
    private static readonly TimeSpan LastNoteGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>When each section ends (chart time), in order; the last is the end of play.</summary>
    public static TimeSpan[] SectionEnds(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var last = chart.HitObjects.Select(static note => note.StartTime)
            .Concat(chart.LongNotes.Select(static note => note.EndTime)).DefaultIfEmpty(chart.Duration).Max();
        var end = last + LastNoteGrace;
        var count = last < ShortSong ? 3 : 5;
        var bars = chart.BarLines.Select(static bar => bar.Time).Where(time => time > TimeSpan.Zero && time < last).ToArray();
        var ends = new TimeSpan[count];
        for (var section = 1; section < count; section++)
        {
            var target = last * section / count;
            // The bar line nearest the even split, past the previous section's end.
            var after = section == 1 ? TimeSpan.Zero : ends[section - 2];
            ends[section - 1] = bars.Where(bar => bar > after).OrderBy(bar => (bar - target).Duration()).Cast<TimeSpan?>().FirstOrDefault() ?? target;
        }
        ends[^1] = end;
        return ends;
    }

    /// <summary>
    /// A 良's and a 可's worth in territory. ponytail: per course from the wiki's examples (easy/normal 5/3, hard 3/2 as
    /// traced, oni 3/1); the wiki has them per chart.
    /// </summary>
    public static (int Great, int Good) Weights(TaikoCourse course) => course switch
    {
        TaikoCourse.Easy or TaikoCourse.Normal => (5, 3),
        TaikoCourse.Hard => (3, 2),
        _ => (3, 1),
    };

    public static int Territory(TaikoCourse course, int great, int good) =>
        Weights(course) is var (a, b) ? great * a + good * b : 0;
}
