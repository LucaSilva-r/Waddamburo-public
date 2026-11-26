using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Library;
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
    // The player's own series (CustomDans: the TJA library's Dani folder), after the server's.
    private volatile List<DanSeries> _custom = [];
    private readonly Func<Task<List<DanSeries>>> _loadCustom;

    public DanCourses(SongCatalogSnapshot snapshot, ChartHashes hashes, string? path, Uri? server, bool insecure,
        string? tjaRoot = null, Func<ChartKey, CatalogAssetKey, CancellationToken, ValueTask<PlayableChart>>? loadChart = null)
    {
        _snapshot = snapshot;
        _hashes = hashes;
        _path = path;
        _loadCustom = loadChart is null ? static () => Task.FromResult(new List<DanSeries>())
            : () => CustomDans.LoadAsync(tjaRoot, snapshot, hashes, loadChart);
        _catalogue = path is null ? null : DanCatalogue.Load(path);
        _ = Task.Run(() => refreshAsync(server, insecure));
    }

    /// <summary>Whether the dan dojo folder has anything to open.</summary>
    public bool Available => _catalogue is not null || _custom.Count > 0;

    // Every series: the server's, then the player's own.
    private IEnumerable<DanSeries> series() => (_catalogue?.Series ?? []).Concat(_custom);

    /// <summary>Whether the library has been matched against the catalogue, so a board's lock means something.</summary>
    public bool Ready => _ready;

    private volatile bool _ready;

    /// <summary>A series' courses (the server's or the player's own) with each one's status against the library.</summary>
    public IReadOnlyList<DanCourseStatus> Courses(string series)
    {
        var library = _library;
        return [.. this.series().FirstOrDefault(candidate => candidate.Id == series)?.Courses.Select(course => DanCourseStatus.Of(course, library)) ?? []];
    }

    /// <summary>The library's song a course song stands for; null: not in the library.</summary>
    public SongDescriptor? SongOf(DanSongStatus status) =>
        _snapshot.Songs.Values.FirstOrDefault(candidate => candidate.Key.StableId == status.Song.SongKey);

    /// <summary>
    /// The dojo as the library tree shows it (free-movies.md, Song select): a folder per series, its courses
    /// in it, each course's songs as its charts with the stars of the course they are played on. A course's Tag is its
    /// status, a series folder's its id.
    /// </summary>
    public IReadOnlyList<LibraryNode> Tree()
    {
        var folders = new List<LibraryNode>();
        foreach (var (id, name) in Series)
        {
            var folder = new LibraryNode(LibraryNodeKind.Folder, name) { Tag = id };
            foreach (var course in _catalogue?.Series.Find(candidate => candidate.Id == id)?.Courses ?? [])
            {
                var status = DanCourseStatus.Of(course, _library);
                var node = new LibraryNode(LibraryNodeKind.Course, course.Name) { Playable = status.Playable, Tag = status };
                foreach (var song in status.Songs)
                {
                    var descriptor = _snapshot.Songs.Values.FirstOrDefault(candidate => candidate.Key.StableId == song.Song.SongKey);
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
        foreach (var candidate in this.series().FirstOrDefault(entry => entry.Id == series)?.Courses ?? [])
        {
            if (candidate.Rank == DanRanks.Challenge)
                challenges++;
            if (candidate == course)
                return course.Rank == DanRanks.Challenge ? challenges : 0;
        }
        return 0;
    }

    /// <summary>The series, newest first, with the names their spines show.</summary>
    public IReadOnlyList<(string Id, string Name)> Series =>
        [.. series().Select(static series => (series.Id, series.Id.StartsWith(CustomDans.Prefix, StringComparison.Ordinal)
            ? series.Name : SeriesName(series.Id)))];

    private static string SeriesName(string id) => id.StartsWith("nijiiro-", StringComparison.Ordinal)
        ? (Strings.Language.StartsWith("ja", StringComparison.Ordinal) ? "ニジイロ" : "Nijiiro ") + id["nijiiro-".Length..]
        : Strings.Language.StartsWith("ja", StringComparison.Ordinal) ? id switch
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

    /// <summary>
    /// The song's genre as dani_select, dani_result and song_info number it (GENRE_JPOP 0 … GENRE_ORIGINAL 7): its
    /// folder's genre, a stock one first. It was the stock folder's position, which follows musicinfo's order, so
    /// the select screen and the songs disagreed (reported on Discord).
    /// </summary>
    public int Genre(SongDescriptor? song) => genre(song);

    private int genre(SongDescriptor? song) => song is null ? 0
        : _snapshot.Categories.Where(category => category.Songs.Contains(song.Key))
            .OrderBy(static category => category.Key.Source == SongSourceKind.Stock ? 0 : 1)
            .Select(static category => (int?)Waddamburo.Game.Scenes.GameplaySceneComposition.GenreIndex(category.Genre ?? category.Name)).FirstOrDefault() ?? 0;

    /// <summary>Matches the library against the courses again (after the library was hashed again).</summary>
    public void Rematch() => _ = Task.Run(async () =>
    {
        _custom = await _loadCustom().ConfigureAwait(false);
        _library = await libraryAsync().ConfigureAwait(false);
    });

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
        try
        {
            _custom = await _loadCustom().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.WriteLine($"Custom dan courses not loaded: {exception.Message}");
        }
        _library = await libraryAsync().ConfigureAwait(false);
        _ready = true;
    }

    // ponytail: only the songs the catalogue names, so an approved custom chart (another song id) is not found
    // yet; go through the whole library's stored hashes when approvals arrive.
    private async Task<IReadOnlyCollection<DanLibraryChart>> libraryAsync()
    {
        var named = series().SelectMany(static series => series.Courses).SelectMany(static course => course.Songs)
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
