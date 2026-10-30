using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Dan;

/// <summary>TaikOnline's dan dojo catalogue (GET api/wdb/dan): every series' courses, as served.</summary>
public sealed record DanCatalogue(List<DanSeries> Series)
{
    /// <summary>The catalogue saved at <paramref name="path"/> (null: none yet, or unreadable).</summary>
    public static DanCatalogue? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ScoreJson.Default.DanCatalogue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps the catalogue for offline starts; a failed save only costs that.</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, ScoreJson.Default.DanCatalogue));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not save the dan courses: {exception.Message}");
        }
    }
}

/// <summary>One game version's list of courses.</summary>
public sealed record DanSeries(string Id, string Name, List<DanCourse> Courses);

/// <summary>
/// A course: its level is the list's slot (1-25 ranks, above them extras). <paramref name="Hash"/> changes
/// with its songs or requirements, so a pass never carries over to a changed course.
/// </summary>
public sealed record DanCourse(int Level, string Name, string Hash, List<DanRequirement> Requirements, List<DanCourseSong> Songs);

/// <summary>
/// One requirement: red and gold hold a value for the whole course, or one per song. 0 = none at that level
/// or for that song. "bads" and "oks" must stay under their value, the rest reach it.
/// </summary>
public sealed record DanRequirement(string Type, int[] Red, int[] Gold);

/// <summary>A course's song: the game's song id and course, and the chart hashes that count for it.</summary>
public sealed record DanCourseSong(string SongKey, int Course, string? Title, int Notes, List<string> Charts);

/// <summary>A chart of the player's library, for matching against the catalogue.</summary>
public sealed record DanLibraryChart(string SongKey, int Course, ChartKey Chart, string Sha256);

public enum DanSongState
{
    /// <summary>The library has a chart that counts for this song.</summary>
    Playable,
    /// <summary>The library has this song and course, but as a chart that does not count.</summary>
    DifferentChart,
    Missing,
}

/// <summary>A course song against the library: <see cref="Chart"/> is what to play when playable.</summary>
public sealed record DanSongStatus(DanCourseSong Song, DanSongState State, ChartKey? Chart, string? Sha256 = null);

/// <summary>A course is playable only when all its songs are: a play that could not count is never offered.</summary>
public sealed record DanCourseStatus(DanCourse Course, IReadOnlyList<DanSongStatus> Songs)
{
    public bool Playable => Songs.All(static song => song.State == DanSongState.Playable);

    /// <summary>The player's charts against a course. A chart counts by its hash alone, whatever its song id.</summary>
    public static DanCourseStatus Of(DanCourse course, IReadOnlyCollection<DanLibraryChart> library)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(library);
        return new DanCourseStatus(course, course.Songs.Select(song =>
        {
            var counted = library.FirstOrDefault(chart => song.Charts.Contains(chart.Sha256, StringComparer.Ordinal));
            if (counted is not null)
                return new DanSongStatus(song, DanSongState.Playable, counted.Chart, counted.Sha256);
            return new DanSongStatus(song, library.Any(chart => chart.Course == song.Course
                && string.Equals(chart.SongKey, song.SongKey, StringComparison.Ordinal))
                ? DanSongState.DifferentChart : DanSongState.Missing, null);
        }).ToList());
    }
}
