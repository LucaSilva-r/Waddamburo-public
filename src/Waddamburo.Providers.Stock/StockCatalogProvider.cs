using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// The game's own songs under a user-supplied <c>data</c> folder: metadata from a
/// <c>musicinfo.xml</c>, solo charts from <c>fumen/&lt;id&gt;/solo/&lt;id&gt;_&lt;e|n|h|m|x&gt;.bin</c>,
/// music from <c>sound/bgm/nub/SONG_&lt;ID&gt;.nub</c> and its preview cue from the matching
/// <c>sound/bgm/nsh/SONG_&lt;ID&gt;.nsh</c>. A song is listed when at least one solo chart exists.
/// </summary>
public sealed partial class StockCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const long MaximumMetadataBytes = 64 * 1024 * 1024;
    private const long MaximumChartBytes = 16 * 1024 * 1024;
    private const int MaximumMeasures = 100_000;
    private const int MaximumNotes = 1_000_000;
    private const string Courses = "enhmx";

    private readonly string _root;
    // Star ratings of the last scan, for the charts it published.
    private IReadOnlyDictionary<string, int[]> _stars = new Dictionary<string, int[]>();

    public StockCatalogProvider(string dataRoot, CatalogProviderId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        Id = id ?? new CatalogProviderId("stock");
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.Stock;

    /// <summary>True when the folder holds metadata and charts this provider can read.</summary>
    public static bool IsStockData(string dataRoot) =>
        Directory.Exists(Path.Combine(dataRoot, "fumen")) && metadataCandidates(dataRoot).Any();

    public ValueTask<SongCatalogContribution> ScanAsync(
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new CatalogScanProgress(Id, "discover", 0, null));
        var diagnostics = new List<CatalogDiagnostic>();
        // Installs carry one musicinfo.xml per game revision; the one describing the most installed
        // charts is the installed revision's.
        var entries = metadataCandidates(_root)
            .Select(path => (Path: path, Entries: readMetadata(path, diagnostics)))
            .OrderByDescending(candidate => candidate.Entries.Count(entry => installedCourses(entry.Id).Any()))
            .ThenBy(static candidate => candidate.Path, StringComparer.Ordinal)
            .FirstOrDefault().Entries
            ?? throw new FileNotFoundException("The stock data folder has no musicinfo.xml.");

        var stars = readStars(Path.Combine(_root, "fumen", "tuning.bin"), diagnostics);
        _stars = stars;
        var songs = new List<SongDescriptor>();
        var categories = new List<(string Genre, List<SongKey> Songs)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogScanProgress(Id, "inspect", index, entries.Count));
            var entry = entries[index];
            if (!seen.Add(entry.Id))
            {
                diagnostics.Add(warning("STOCK_DUPLICATE_SONG", $"Music id '{entry.Id}' is listed more than once."));
                continue;
            }
            var key = new SongKey(Source, entry.Id);
            var charts = installedCourses(entry.Id)
                .Select(course => new SongChartDescriptor(
                    new ChartKey(key, course.ToString().ToLowerInvariant()),
                    course.ToString(),
                    new CatalogAssetKey(Id, $"chart:{entry.Id}:{Courses[(int)course]}"),
                    course,
                    level(stars, entry.Id, course)))
                .ToArray();
            if (charts.Length == 0)
                continue;
            var bank = "SONG_" + entry.Id.ToUpperInvariant();
            var hasAudio = File.Exists(Path.Combine(_root, "sound", "bgm", "nub", bank + ".nub"));
            if (!hasAudio)
                diagnostics.Add(warning("STOCK_AUDIO_MISSING", $"Music id '{entry.Id}' has no installed music."));
            songs.Add(new SongDescriptor(
                key,
                new SongTitle(entry.Title ?? entry.Id, entry.Title),
                null,
                charts,
                hasAudio ? new CatalogAssetKey(Id, $"audio:{entry.Id}") : null,
                hasAudio ? previewStart(Path.Combine(_root, "sound", "bgm", "nsh", bank + ".nsh")) : null));
            var genre = entry.Genre ?? "";
            var category = categories.FindIndex(category => category.Genre == genre);
            if (category < 0)
                categories.Add((genre, [key]));
            else
                categories[category].Songs.Add(key);
        }

        progress?.Report(new CatalogScanProgress(Id, "complete", entries.Count, entries.Count));
        return ValueTask.FromResult(new SongCatalogContribution(
            Id,
            Source,
            songs,
            categories.Select((category, index) => new SongCategoryDescriptor(
                new CategoryKey(Source, category.Genre.Length == 0 ? "-" : category.Genre),
                categoryName(category.Genre),
                index,
                category.Songs)),
            diagnostics));
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id)
            throw new ArgumentException("The asset belongs to a different catalog provider.", nameof(asset));
        cancellationToken.ThrowIfCancellationRequested();
        var path = assetPath(asset);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public async ValueTask<PlayableChart> LoadChartAsync(
        ChartKey chart,
        CatalogAssetKey asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(asset);
        if (chart.Song.Source != Source)
            throw new ArgumentException("The chart does not belong to the stock source.", nameof(chart));
        if (asset.Provider != Id || !asset.StableId.StartsWith("chart:", StringComparison.Ordinal))
            throw new ArgumentException("The asset is not a stock chart of this provider.", nameof(asset));
        var path = assetPath(asset);
        if (new FileInfo(path).Length > MaximumChartBytes)
            throw new InvalidDataException("The selected fumen exceeds the chart-size limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var course = (TaikoCourse)Courses.IndexOf(asset.StableId[^1], StringComparison.Ordinal);
        return FumenChartReader.Read(bytes, chart, MaximumMeasures, MaximumNotes) with
        {
            Level = level(_stars, asset.StableId.Split(':')[1], course),
        };
    }

    // "chart:<id>:<course letter>" or "audio:<id>"; ids are validated so a key cannot leave the root.
    private string assetPath(CatalogAssetKey asset)
    {
        var fields = asset.StableId.Split(':');
        if (fields.Length >= 2 && MusicId().IsMatch(fields[1]))
        {
            var id = fields[1];
            if (fields is ["chart", _, [var course]] && Courses.Contains(course, StringComparison.Ordinal))
                return chartPath(id, course);
            if (fields.Length == 2 && fields[0] == "audio")
                return Path.Combine(_root, "sound", "bgm", "nub", $"SONG_{id.ToUpperInvariant()}.nub");
        }
        throw new ArgumentException("The stock asset key is malformed.", nameof(asset));
    }

    private string chartPath(string id, char course) => Path.Combine(_root, "fumen", id, "solo", $"{id}_{course}.bin");

    private IEnumerable<TaikoCourse> installedCourses(string id) =>
        Enumerable.Range(0, Courses.Length)
            .Where(course => File.Exists(chartPath(id, Courses[course])))
            .Select(static course => (TaikoCourse)course);

    private static IEnumerable<string> metadataCandidates(string root)
    {
        var top = Path.Combine(root, "musicinfo.xml");
        var config = Path.Combine(root, "config");
        var revisions = Directory.Exists(config)
            ? Directory.EnumerateDirectories(config).Select(directory => Path.Combine(directory, "musicinfo.xml"))
            : [];
        return revisions.Append(top).Where(File.Exists);
    }

    private sealed record MetadataEntry(string Id, string? Title, string? Genre);

    // Boost XML serialisation: <MusicInfo><Data><musicid/><musicname/><genrename/>…</Data>…
    private List<MetadataEntry> readMetadata(string path, List<CatalogDiagnostic> diagnostics)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumMetadataBytes,
            };
            using var reader = XmlReader.Create(path, settings);
            var entries = new List<MetadataEntry>();
            foreach (var data in XDocument.Load(reader).Descendants("Data"))
            {
                var id = ((string?)data.Element("musicid"))?.Trim();
                if (id is null || !MusicId().IsMatch(id))
                {
                    if (id is not null)
                        diagnostics.Add(warning("STOCK_ID_INVALID", $"Music id '{id}' is not a supported identifier."));
                    continue;
                }
                entries.Add(new MetadataEntry(id, blank((string?)data.Element("musicname")), blank((string?)data.Element("genrename"))));
            }
            return entries;
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(warning("STOCK_METADATA_INVALID", $"A musicinfo.xml could not be read: {exception.Message}"));
            return [];
        }
    }

    /// <summary>
    /// Preview cue of a song bank: the <c>.nsh</c> (magic 0x00020100, big-endian) holds a table
    /// offset at 0x18 whose first word points to the "at3" stream entry; the entry's 20-byte user
    /// data (length at +0x90) ends with the preview start in milliseconds (+0xB0).
    /// </summary>
    internal static TimeSpan? PreviewStart(ReadOnlySpan<byte> nsh)
    {
        uint word(ReadOnlySpan<byte> data, long at) => BinaryPrimitives.ReadUInt32BigEndian(data[(int)at..]);
        if (nsh.Length < 0x24 || word(nsh, 0) != 0x00020100)
            return null;
        long table = word(nsh, 0x18);
        if (table > nsh.Length - 4)
            return null;
        long entry = word(nsh, table);
        if (entry > nsh.Length - 0xB4 || word(nsh, entry) != 0x61743300 || word(nsh, entry + 0x90) != 20)
            return null;
        return TimeSpan.FromMilliseconds(word(nsh, entry + 0xB0));
    }

    private static TimeSpan? previewStart(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024
                ? PreviewStart(File.ReadAllBytes(path))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int? level(IReadOnlyDictionary<string, int[]> stars, string id, TaikoCourse course) =>
        stars.TryGetValue(id, out var courses) && courses[(int)course] > 0 ? courses[(int)course] : null;

    /// <summary>
    /// Authored star ratings from <c>fumen/tuning.bin</c> (big-endian): a record count, then
    /// 0x90C-byte records, then a string pool addressed by offsets. A record starts with the
    /// offsets of its music id, bank name and title, followed by course entries of 0x80 bytes
    /// (name offset, stars) named after the record: <c>&lt;id&gt;1p_&lt;e|n|h|m|x&gt;</c>. Ura is a
    /// separate <c>ex_&lt;id&gt;</c> record whose Oni entry (<c>ex_&lt;id&gt;1p_m</c>) rates <c>&lt;id&gt;_x.bin</c>.
    /// </summary>
    internal static Dictionary<string, int[]> ReadStars(ReadOnlySpan<byte> data)
    {
        const int RecordSize = 0x90C, CourseOffset = 12, CourseSize = 0x80, CourseEntries = 18;
        var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
        if (data.Length < 4)
            return result;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (count > (uint)(data.Length - 4) / RecordSize)
            return result;
        var pool = data[(4 + (int)count * RecordSize)..];
        static string? text(ReadOnlySpan<byte> pool, uint offset)
        {
            if (offset >= (uint)pool.Length)
                return null;
            var end = pool[(int)offset..].IndexOf((byte)0);
            return end < 0 ? null : System.Text.Encoding.UTF8.GetString(pool.Slice((int)offset, end));
        }
        for (var index = 0; index < count; index++)
        {
            var record = data.Slice(4 + index * RecordSize, RecordSize);
            if (text(pool, BinaryPrimitives.ReadUInt32BigEndian(record)) is not { Length: > 0 } name)
                continue;
            var ura = name.StartsWith("ex_", StringComparison.Ordinal);
            var id = ura ? name[3..] : name;
            if (id.Length == 0)
                continue;
            if (!result.TryGetValue(id, out var courses))
                result[id] = courses = new int[Courses.Length];
            // Courses beyond the first nine are duet entries.
            for (var entry = 0; entry < CourseEntries / 2; entry++)
            {
                var course = record.Slice(CourseOffset + entry * CourseSize, 8);
                var stars = BinaryPrimitives.ReadUInt32BigEndian(course[4..]);
                if (stars is 0 or > 10 || text(pool, BinaryPrimitives.ReadUInt32BigEndian(course)) is not { } courseName
                    || !courseName.StartsWith(name + "1p_", StringComparison.Ordinal) || courseName.Length != name.Length + 4)
                    continue;
                var letter = Courses.IndexOf(courseName[^1], StringComparison.Ordinal);
                if (letter < 0)
                    continue;
                if (!ura)
                    courses[letter] = (int)stars;
                else if (letter == (int)TaikoCourse.Oni)
                    courses[(int)TaikoCourse.Ura] = (int)stars;
            }
        }
        return result;
    }

    private Dictionary<string, int[]> readStars(string path, List<CatalogDiagnostic> diagnostics)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= MaximumMetadataBytes)
                return ReadStars(File.ReadAllBytes(path));
            diagnostics.Add(warning("STOCK_STARS_MISSING", "fumen/tuning.bin is missing; courses have no star rating."));
        }
        catch (IOException exception)
        {
            diagnostics.Add(warning("STOCK_STARS_INVALID", $"fumen/tuning.bin could not be read: {exception.Message}"));
        }
        return [];
    }

    // The metadata's genre names, as the Song Select categories name them.
    private static string categoryName(string genre) => genre switch
    {
        "J-POP" => "J-POP",
        "アニメ" => "Anime",
        "ボーカロイド" => "Vocaloid",
        "童謡" => "Kids",
        "バラエティ" => "Variety",
        "クラシック" => "Classical",
        "ゲームミュージック" => "Game Music",
        "ナムコオリジナル" => "Namco Original",
        "メドレー" => "Medley",
        "" => "Uncategorized",
        _ => genre,
    };

    private static string? blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private CatalogDiagnostic warning(string code, string message) =>
        new(CatalogDiagnosticSeverity.Warning, code, message, Id);

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex MusicId();
}
