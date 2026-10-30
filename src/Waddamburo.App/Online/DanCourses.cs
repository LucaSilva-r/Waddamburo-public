using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Online;

/// <summary>
/// The dan dojo's courses against this library: the catalogue saved from TaikOnline (refreshed in the
/// background when online; no account needed) and the hashes of the charts its songs ask for. Without a
/// catalogue, from never having been online, the folder stays away.
/// </summary>
internal sealed class DanCourses
{
    private readonly SongCatalogSnapshot _snapshot;
    private readonly ChartHashes _hashes;
    private readonly string? _path;
    private volatile DanCatalogue? _catalogue;
    private volatile IReadOnlyCollection<DanLibraryChart> _library = [];

    public DanCourses(SongCatalogSnapshot snapshot, ChartHashes hashes, string? path, Uri? server, bool insecure)
    {
        _snapshot = snapshot;
        _hashes = hashes;
        _path = path;
        _catalogue = path is null ? null : DanCatalogue.Load(path);
        _ = Task.Run(() => refreshAsync(server, insecure));
    }

    /// <summary>Whether the dan dojo folder has anything to open.</summary>
    public bool Available => _catalogue is not null;

    /// <summary>Whether the library has been matched against the catalogue, so a board's lock means something.</summary>
    public bool Ready => _ready;

    private volatile bool _ready;

    /// <summary>The song a board's uid stands for, to draw its title.</summary>
    public SongDescriptor? Song(int uid) => (uint)uid < (uint)_uids.Count ? _uids[uid] : null;

    private readonly List<SongDescriptor> _uids = [];

    /// <summary>A series' ranks as dani_select's boards, the back board first, with each course's status.</summary>
    // ponytail: the ranks of Green only, the extras (level above 25, named by a texture) and the picker come next.
    public (IReadOnlyList<DaniSelectBoard> Boards, IReadOnlyList<DanCourseStatus?> Courses) Boards(string series)
    {
        var boards = new List<DaniSelectBoard> { new(DaniSelectHostBinding.BackId, true, []) };
        var courses = new List<DanCourseStatus?> { null };
        _uids.Clear();
        var catalogue = _catalogue;
        var ranks = catalogue?.Series.Find(candidate => candidate.Id == series)?.Courses.Where(static course => course.Level <= 25) ?? [];
        var library = _library;
        foreach (var course in ranks)
        {
            var status = DanCourseStatus.Of(course, library);
            boards.Add(new DaniSelectBoard(course.Level - 1, status.Playable, [.. status.Songs.Select(board)],
                course.Songs.Sum(static song => song.Notes), conditions(course)));
            courses.Add(status);
        }
        return (boards, courses);
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

    private DaniSelectSong board(DanSongStatus status)
    {
        var song = _snapshot.Songs.Values.FirstOrDefault(candidate => candidate.Key.StableId == status.Song.SongKey);
        var chart = song?.Charts.FirstOrDefault(candidate => (int?)candidate.Course == status.Song.Course);
        _uids.Add(song!);
        return new DaniSelectSong(_uids.Count - 1, genre(song), status.Song.Course, chart?.Level ?? 0);
    }

    // The song's folder among the game's genres, for its colour.
    public int Genre(SongDescriptor song) => genre(song);

    private int genre(SongDescriptor? song)
    {
        if (song is null)
            return 0;
        var stock = _snapshot.Categories.Where(static category => category.Key.Source == SongSourceKind.Stock).ToList();
        return Math.Max(stock.FindIndex(category => category.Songs.Contains(song.Key)), 0);
    }

    private async Task refreshAsync(Uri? server, bool insecure)
    {
        if (server is not null)
        {
            try
            {
                using var http = ScoreClient.CreateHttp(server, insecure);
                var fresh = await new ScoreClient(http).DanCatalogueAsync().ConfigureAwait(false);
                _catalogue = fresh;
                if (_path is not null)
                    fresh.Save(_path);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"Dan courses not refreshed: {exception.Message}");
            }
        }
        if (_catalogue is { } catalogue)
            _library = await libraryAsync(catalogue).ConfigureAwait(false);
        _ready = true;
    }

    // ponytail: only the songs the catalogue names, so an approved custom chart (another song id) is not found
    // yet; go through the whole library's stored hashes when approvals arrive.
    private async Task<IReadOnlyCollection<DanLibraryChart>> libraryAsync(DanCatalogue catalogue)
    {
        var named = catalogue.Series.SelectMany(static series => series.Courses).SelectMany(static course => course.Songs)
            .Select(static song => song.SongKey).ToHashSet(StringComparer.Ordinal);
        var library = new List<DanLibraryChart>();
        foreach (var song in _snapshot.Songs.Values.Where(song => named.Contains(song.Key.StableId)))
            foreach (var chart in song.Charts.Where(static chart => chart.Course is not null))
            {
                try
                {
                    if (await _hashes.HashAsync(chart).ConfigureAwait(false) is { } hash)
                        library.Add(new DanLibraryChart(song.Key.StableId, (int)chart.Course!.Value, chart.Key, hash));
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException
                    or IOException or OverflowException or ArgumentException)
                {
                    Console.WriteLine($"Dan courses: unreadable chart {chart.Key}: {exception.Message}");
                }
            }
        return library;
    }
}
