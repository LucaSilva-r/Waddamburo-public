namespace Waddamburo.Game.Dan;

/// <summary>One song's counts within a course play.</summary>
public readonly record struct DanSongCounts(int Goods, int Oks, int Bads, int Rolls);

/// <summary>One song's row on the course's results.</summary>
public readonly record struct DanSongResult(int Genre, int Course, int Level, DanSongCounts Counts);

/// <summary>A finished course play, as dani_result shows it.</summary>
public sealed record DanCourseResult(DanCourse Course, IReadOnlyList<DanSongResult> Songs, int SongsPlayed, DanProgress Total, int Level)
{
    /// <summary>The movie's result levels (traced): 0 failed, 1 red, 2 gold.</summary>
    public static int LevelOf(DanCourse course, in DanProgress total, IReadOnlyList<DanProgress>? songEnds = null) =>
        !DanExams.Passes(course, total, gold: false, songEnds) ? 0 : DanExams.Passes(course, total, gold: true, songEnds) ? 2 : 1;

    /// <summary>The progress at each finished song's end.</summary>
    public IReadOnlyList<DanProgress> SongEnds { get; init; } = [];
}
