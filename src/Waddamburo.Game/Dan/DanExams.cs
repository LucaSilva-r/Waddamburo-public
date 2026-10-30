namespace Waddamburo.Game.Dan;

/// <summary>A course play so far, as its requirements read it.</summary>
public readonly record struct DanProgress(int GaugePercent, int Goods, int Oks, int Bads, int MaxCombo, int Rolls, long Score)
{
    public long Hits => Goods + Oks + Rolls;
}

/// <summary>
/// A course's requirements against a play. Up to Green they cover the whole course. "bads" and "oks" must stay
/// under their value and can fail mid-course, as can goods and combo once the notes left cannot reach them;
/// gauge, hits, score and rolls are judged at the end. Types are the movies' ConditionInfo numbers.
/// </summary>
public sealed class DanExams
{
    private readonly int _notes;

    public DanExams(DanCourse course)
    {
        ArgumentNullException.ThrowIfNull(course);
        _notes = course.Songs.Sum(static song => song.Notes);
        Requirements = [.. course.Requirements.Where(static requirement => requirement.Red.Length > 0 && requirement.Red[0] != 0)];
    }

    /// <summary>What the panel shows: the requirements red asks for.</summary>
    public IReadOnlyList<DanRequirement> Requirements { get; }

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

    /// <summary>False once the requirement can no longer be met.</summary>
    public bool StillPossible(DanRequirement requirement, in DanProgress progress)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var value = ValueOf(requirement.Type, progress);
        var target = requirement.Red[0];
        return requirement.Type switch
        {
            "bads" or "oks" => value < target,
            "goods" or "combo" => value + NotesLeft(progress) >= target,
            _ => true,
        };
    }

    /// <summary>Whether the finished play passes: every requirement of red, or of gold when asked.</summary>
    public static bool Passes(DanCourse course, in DanProgress progress, bool gold)
    {
        ArgumentNullException.ThrowIfNull(course);
        foreach (var requirement in course.Requirements)
        {
            var target = (gold ? requirement.Gold : requirement.Red)[0];
            if (target == 0)
                continue;
            var value = ValueOf(requirement.Type, progress);
            if (requirement.Type is "bads" or "oks" ? value >= target : value < target)
                return false;
        }
        return true;
    }
}
