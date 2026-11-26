using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Library;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Dan;

/// <summary>
/// The dan dojo: every provider's series matched against the library (a course is playable once all its songs are
/// found), the credit's series and course, the finished run's result, each player's bests, and keeping a passed run
/// (it uploads with the next sync; the server decides the badge).
/// </summary>
/// <remarks><paramref name="japanese"/>: The series' names in Japanese.</remarks>
public sealed class DanModule(CreditState credit, Func<ScoreStore?> scores, Func<bool> japanese)
{
    private IReadOnlyList<IDanCourseProvider> _providers = [];
    private DanLibrary? _library;
    private volatile IReadOnlyList<DanSeries>[] _series = [];
    private volatile IReadOnlyCollection<DanLibraryChart> _charts = [];
    private volatile bool _ready;

    /// <summary>
    /// Starts the dojo on a library: each provider's saved series at once, then their fresh ones and the library's
    /// charts in the background (<see cref="Ready"/> once done).
    /// </summary>
    public void Load(DanLibrary library, params IReadOnlyList<IDanCourseProvider> providers)
    {
        (_library, _providers) = (library, providers);
        _series = [.. providers.Select(static provider => provider.Saved)];
        _ = Task.Run(async () =>
        {
            await refreshAsync().ConfigureAwait(false);
            _ready = true;
        });
    }

    /// <summary>Asks every provider again and matches the library again (after the library was hashed again).</summary>
    public void Rematch() => _ = Task.Run(refreshAsync);

    private async Task refreshAsync()
    {
        if (_library is not { } library)
            return;
        var series = new IReadOnlyList<DanSeries>[_providers.Count];
        for (var index = 0; index < series.Length; index++)
            series[index] = await _providers[index].LoadAsync(library).ConfigureAwait(false);
        _series = series;
        _charts = await chartsAsync(library).ConfigureAwait(false);
    }

    // Every series, the providers' in order.
    private IEnumerable<DanSeries> allSeries() => _series.SelectMany(static series => series);

    /// <summary>Whether the dojo has anything to open.</summary>
    public bool Available => allSeries().Any();

    /// <summary>Whether the library has been matched against the courses, so a course's lock means something.</summary>
    public bool Ready => _ready;

    /// <summary>The series, in the providers' order, with the names their folders show.</summary>
    public IReadOnlyList<(string Id, string Name)> AllSeries =>
        [.. allSeries().Select(series => (series.Id, series.Id.StartsWith(TjaDanCourses.Prefix, StringComparison.Ordinal)
            ? series.Name : SeriesName(series.Id, japanese())))];

    /// <summary>A series' courses with each one's status against the library.</summary>
    public IReadOnlyList<DanCourseStatus> Courses(string series)
    {
        var charts = _charts;
        return [.. allSeries().FirstOrDefault(candidate => candidate.Id == series)?.Courses.Select(course => DanCourseStatus.Of(course, charts)) ?? []];
    }

    /// <summary>The library's song a course song stands for; null: not in the library.</summary>
    public SongDescriptor? SongOf(DanSongStatus status) =>
        _library?.Snapshot.Songs.Values.FirstOrDefault(candidate => candidate.Key.StableId == status.Song.SongKey);

    /// <summary>
    /// The dojo as the library tree shows it: a folder per series, its courses in it, each course's songs as its charts
    /// with the stars of the course they are played on. A course's Tag is its status, a series folder's its id.
    /// </summary>
    public IReadOnlyList<LibraryNode> Tree()
    {
        var folders = new List<LibraryNode>();
        foreach (var (id, name) in AllSeries)
        {
            var folder = new LibraryNode(LibraryNodeKind.Folder, name) { Tag = id };
            foreach (var status in Courses(id))
            {
                var node = new LibraryNode(LibraryNodeKind.Course, status.Course.Name) { Playable = status.Playable, Tag = status };
                foreach (var song in status.Songs)
                {
                    var descriptor = SongOf(song);
                    var level = descriptor?.Charts.FirstOrDefault(candidate => (int?)candidate.Course == song.Song.Course)?.Level;
                    node.Children.Add(new LibraryNode(LibraryNodeKind.Chart, descriptor?.Title.English ?? descriptor?.Title.Primary ?? song.Song.Title ?? song.Song.SongKey)
                    {
                        Stars = level,
                        Course = ((TaikoCourse)song.Song.Course).ToString().ToLowerInvariant(),
                        Playable = song.State == DanSongState.Playable,
                    });
                }
                folder.Children.Add(node);
            }
            folders.Add(folder);
        }
        return folders;
    }

    /// <summary>A challenge course's number in its series, for its art; 0: a rank.</summary>
    public int ChallengeNumber(string series, DanCourse course)
    {
        var challenges = 0;
        foreach (var candidate in allSeries().FirstOrDefault(entry => entry.Id == series)?.Courses ?? [])
        {
            if (candidate.Rank == DanRanks.Challenge)
                challenges++;
            if (candidate == course)
                return course.Rank == DanRanks.Challenge ? challenges : 0;
        }
        return 0;
    }

    /// <summary>
    /// The song's genre as dani_select, dani_result and song_info number it (GENRE_JPOP 0 … GENRE_ORIGINAL 7): its
    /// folder's genre, a stock one first.
    /// </summary>
    public int Genre(SongDescriptor? song) => song is null || _library is not { } library ? 0
        : library.Snapshot.Categories.Where(category => category.Songs.Contains(song.Key))
            .OrderBy(static category => category.Key.Source == SongSourceKind.Stock ? 0 : 1)
            .Select(static category => (int?)Waddamburo.Game.Scenes.GameplaySceneComposition.GenreIndex(category.Genre ?? category.Name)).FirstOrDefault() ?? 0;

    /// <summary>The series the course list shows.</summary>
    public string Series { get; set; } = "green";

    /// <summary>The challenge course being played: its number in <see cref="Series"/>; 0: a rank.</summary>
    public int Challenge { get; set; }

    /// <summary>The course run the next results show; null: a song's.</summary>
    public DanCourseResult? Result { get; set; }

    /// <summary>The playing drum's player's best result per course hash (the course list's stamps).</summary>
    public IReadOnlyDictionary<string, int> Bests() => TaikoGuest.Profiles[credit.PlayerSide] is { } player && scores() is { } store
        ? store.DanBests(player.Baid) : new Dictionary<string, int>();

    /// <summary>
    /// Keeps a finished run (failed runs aren't kept, here or on the server). False: not kept (failed, no store, or the
    /// store refused it, logged: a lost score must not end the credit).
    /// </summary>
    public bool Keep(DanPlayRecord record)
    {
        if (record.Result.Level == 0 || scores() is not { } store)
            return false;
        try
        {
            store.SaveDan(record);
            return true;
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
            return false;
        }
    }

    // ponytail: only the songs the courses name, so an approved custom chart (another song id) is not found yet; go
    // through the whole library's stored hashes when approvals arrive.
    private async Task<IReadOnlyCollection<DanLibraryChart>> chartsAsync(DanLibrary library)
    {
        var named = allSeries().SelectMany(static series => series.Courses).SelectMany(static course => course.Songs)
            .Select(static song => song.SongKey).ToHashSet(StringComparer.Ordinal);
        var charts = new List<DanLibraryChart>();
        foreach (var song in library.Snapshot.Songs.Values.Where(song => named.Contains(song.Key.StableId)))
            foreach (var chart in song.Charts.Where(static chart => chart.Course is not null))
            {
                try
                {
                    if (await library.Hashes.HashAsync(chart).ConfigureAwait(false) is { } hash)
                        charts.Add(new DanLibraryChart(song.Key.StableId, (int)chart.Course!.Value, chart.Key, hash));
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException
                    or IOException or OverflowException or ArgumentException)
                {
                    Console.WriteLine($"Dan courses: unreadable chart {chart.Key}: {exception.Message}");
                }
            }
        return charts;
    }

    private static string SeriesName(string id, bool japanese) => id.StartsWith("nijiiro-", StringComparison.Ordinal)
        ? (japanese ? "ニジイロ" : "Nijiiro ") + id["nijiiro-".Length..]
        : japanese ? id switch
    {
        "green" => "グリーン",
        "blue" => "ブルー",
        "yellow" => "イエロー",
        "red" => "レッド",
        "white" => "ホワイト",
        "murasaki" => "ムラサキ",
        "kimidori" => "キミドリ",
        "momoiro" => "モモイロ",
        "sorairo" => "ソライロ",
        "katsudon" => "カツドン",
        _ => id,
    } : id switch
    {
        "green" => "Green",
        "blue" => "Blue",
        "yellow" => "Yellow",
        "red" => "Red",
        "white" => "White",
        "murasaki" => "Murasaki",
        "kimidori" => "Kimidori",
        "momoiro" => "Momoiro",
        "sorairo" => "Sorairo",
        "katsudon" => "Katsu-Don",
        _ => id,
    };
}
