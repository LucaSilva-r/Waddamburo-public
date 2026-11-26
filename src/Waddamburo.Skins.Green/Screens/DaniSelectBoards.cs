using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Skins.Green.Art;

namespace Waddamburo.Skins.Green;

/// <summary>A series' courses as dani_select's boards: the back board first, then each course with its songs and panel.</summary>
public static class DaniSelectBoards
{
    /// <remarks>
    /// <paramref name="courses"/>: The series' courses and their status against the library.<br/>
    /// <paramref name="bests"/>: The player's best result per course hash.<br/>
    /// <paramref name="song"/>: The library's song for a course song.<br/>
    /// <paramref name="genre"/>: A song's genre as the movie numbers it.<br/>
    /// <paramref name="uids"/>: Filled with each board song's song, by its uid (the movie asks for titles by uid).<br/>
    /// </remarks>
    public static List<DaniSelectBoard> Build(string series, IReadOnlyList<DanCourseStatus> courses, IReadOnlyDictionary<string, int>? bests,
        Func<DanSongStatus, SongDescriptor?> song, Func<SongDescriptor?, int> genre, List<SongDescriptor?> uids)
    {
        var boards = new List<DaniSelectBoard> { new(DaniSelectHostBinding.BackId, true, []) };
        uids.Clear();
        var challenges = 0;
        foreach (var status in courses)
        {
            var course = status.Course;
            boards.Add(new DaniSelectBoard(course.Rank, status.Playable, [.. status.Songs.Select(board)],
                course.Songs.Sum(static song => song.Notes), conditions(course),
                // The movie's history labels (traced): 0 none, 1 played (no art), 2 success, 3 perfect.
                bests is not null && bests.TryGetValue(course.Hash, out var best) ? best + 1 : 0)
            {
                Challenge = course.Rank == DanRanks.Challenge ? ++challenges : null,
            });
            if (course.Rank == DanRanks.Challenge)
                DanChallengeArt.Names[(series, challenges)] = course.Name;
        }
        return boards;

        DaniSelectSong board(DanSongStatus status)
        {
            var descriptor = song(status);
            var chart = descriptor?.Charts.FirstOrDefault(candidate => (int?)candidate.Course == status.Song.Course);
            uids.Add(descriptor);
            return new DaniSelectSong(uids.Count - 1, genre(descriptor), status.Song.Course, chart?.Level ?? 0);
        }
    }

    // The board's panel holds one value per requirement, the red one (a value only gold asks for has none).
    private static List<DaniSelectCondition> conditions(DanCourse course) => [.. new DanExams(course).Requirements
        .Where(static requirement => requirement.Red.Length == 1)
        .Select(static requirement => new DaniSelectCondition(requirement.Type switch
        {
            "soul_gauge" => 0,
            "goods" => 1,
            "oks" => 2,
            "bads" => 3,
            "combo" => 4,
            "hits" => 5,
            "score" => 6,
            _ => 7,
        }, requirement.Red[0]))];
}
