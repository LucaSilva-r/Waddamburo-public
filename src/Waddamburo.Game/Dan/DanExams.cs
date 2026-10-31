namespace Waddamburo.Game.Dan;

/// <summary>A course play so far, as its requirements read it.</summary>
public readonly record struct DanProgress(int GaugePercent, int Goods, int Oks, int Bads, int MaxCombo, int Rolls, long Score)
{
    public long Hits => Goods + Oks + Rolls;
}

/// <summary>
/// A course's requirements against a play. Up to Green they cover the whole course; Nijiiro's can have a value per
/// song, counted on that song alone and judged by its end. "bads" and "oks" must stay under their value and can
/// fail mid-course, as can goods and combo once the notes left cannot reach them; gauge, hits, score and rolls are
/// judged at the end. Types are the movies' ConditionInfo numbers.
/// </summary>
public sealed class DanExams
{
    /// <summary>The conditions dani_enso and dani_result have clips for (SetConditionNum refuses more).</summary>
    public const int Slots = 3;

    private readonly int _notes;
    private readonly DanCourse _course;

    public DanExams(DanCourse course)
    {
        ArgumentNullException.ThrowIfNull(course);
        _course = course;
        _notes = course.Songs.Sum(static song => song.Notes);
        // Traced 五級: gauge, hits, then the limit on bads, whatever order the course file lists them in.
        Requirements = [.. course.Requirements.Where(static requirement => requirement.Red.Any(static value => value != 0))
            .OrderBy(static requirement => requirement.Type is "bads" or "oks")];
    }

    /// <summary>The requirements red asks for.</summary>
    public IReadOnlyList<DanRequirement> Requirements { get; }

    /// <summary>
    /// What the panel shows. A course with more requirements than slots (most of Nijiiro's) leaves the gauge out:
    /// the gauge bar above the lane shows it anyway, and it is only judged at the end.
    /// </summary>
    public IReadOnlyList<DanRequirement> OnPanel => Requirements.Count <= Slots ? Requirements
        : [.. Requirements.Where(static requirement => requirement.Type != "soul_gauge").Take(Slots)];

    /// <summary>What <paramref name="values"/> asks of <paramref name="song"/>: the course's one value, or the song's own.</summary>
    public static int Target(int[] values, int song) => values[values.Length == 1 ? 0 : Math.Min(song, values.Length - 1)];

    /// <summary>The requirement's count: the course's, or of the song that began at <paramref name="start"/>.</summary>
    // ponytail: a per-song combo needs the song's own best (only 外伝 has them); this counts growth of the course's.
    public static long ValueOf(DanRequirement requirement, in DanProgress progress, in DanProgress start)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return ValueOf(requirement.Type, progress) - (requirement.Red.Length > 1 ? ValueOf(requirement.Type, start) : 0);
    }

    public int NotesLeft(in DanProgress progress) => Math.Max(_notes - progress.Goods - progress.Oks - progress.Bads, 0);

    public static int TypeOf(string type) => type switch
    {
        "soul_gauge" => 0,
        "goods" => 1,
        "oks" => 2,
        "bads" => 3,
        "combo" => 4,
        "hits" => 5,
        "score" => 6,
        "rolls" => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown dan requirement."),
    };

    public static long ValueOf(string type, in DanProgress progress) => type switch
    {
        "soul_gauge" => progress.GaugePercent,
        "goods" => progress.Goods,
        "oks" => progress.Oks,
        "bads" => progress.Bads,
        "combo" => progress.MaxCombo,
        "hits" => progress.Hits,
        "score" => progress.Score,
        "rolls" => progress.Rolls,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown dan requirement."),
    };

    /// <summary>False once the requirement can no longer be met, on song <paramref name="song"/>, begun at <paramref name="start"/>.</summary>
    public bool StillPossible(DanRequirement requirement, in DanProgress progress, int song = 0, in DanProgress start = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var value = ValueOf(requirement, progress, start);
        var target = Target(requirement.Red, song);
        if (target == 0)
            return true;
        var left = requirement.Red.Length > 1
            ? Math.Max(_course.Songs[Math.Min(song, _course.Songs.Count - 1)].Notes - (progress.Goods - start.Goods) - (progress.Oks - start.Oks) - (progress.Bads - start.Bads), 0)
            : NotesLeft(progress);
        return requirement.Type switch
        {
            "bads" or "oks" => value < target,
            "goods" or "combo" => value + left >= target,
            _ => true,
        };
    }

    /// <summary>A song has ended: whether one of its own requirements was missed, which ends the course there.</summary>
    public bool MissedOnSong(in DanProgress end, int song, in DanProgress start)
    {
        foreach (var requirement in Requirements)
            if (requirement.Red.Length > 1 && !meets(requirement.Type, ValueOf(requirement, end, start), Target(requirement.Red, song)))
                return true;
        return false;
    }

    /// <summary>What the results show, as they have fewer slots than Nijiiro has requirements: missed ones first.</summary>
    public IReadOnlyList<DanRequirement> OnResults(in DanProgress total, IReadOnlyList<DanProgress> songEnds)
    {
        var (totals, ends) = (total, songEnds);
        return [.. Requirements.OrderBy(requirement => Meets(requirement, totals, ends, gold: false)).Take(Slots)];
    }

    /// <summary>
    /// Whether the finished play passes: every requirement of red, or of gold when asked.
    /// <paramref name="songEnds"/> holds the progress at each finished song's end, for per-song requirements.
    /// </summary>
    public static bool Passes(DanCourse course, in DanProgress progress, bool gold, IReadOnlyList<DanProgress>? songEnds = null)
    {
        ArgumentNullException.ThrowIfNull(course);
        foreach (var requirement in course.Requirements)
            if (!Meets(requirement, progress, songEnds ?? [], gold))
                return false;
        return true;
    }

    private static bool Meets(DanRequirement requirement, in DanProgress total, IReadOnlyList<DanProgress> songEnds, bool gold)
    {
        var targets = gold ? requirement.Gold : requirement.Red;
        if (targets.Length == 1)
            return meets(requirement.Type, ValueOf(requirement.Type, total), targets[0]);
        // A song not reached has counted nothing; the last song's end is the total.
        for (var song = 0; song < targets.Length; song++)
        {
            var start = song == 0 ? default : song - 1 < songEnds.Count ? songEnds[song - 1] : total;
            var end = song < songEnds.Count ? songEnds[song] : total;
            if (!meets(requirement.Type, ValueOf(requirement.Type, end) - ValueOf(requirement.Type, start), targets[song]))
                return false;
        }
        return true;
    }

    private static bool meets(string type, long value, int target) =>
        target == 0 || (type is "bads" or "oks" ? value < target : value >= target);
}
