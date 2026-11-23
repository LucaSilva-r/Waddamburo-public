using System.Text.RegularExpressions;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// The songs of a 太鼓の達人Wii (2008) disc the player extracted: the songs <c>sheet/musicinfo</c> lists, their 1P
/// charts from <c>sheet/sht/bin</c>, stars and 配点 from <c>sheet/tuning</c>, music from <c>sound/SONG_&lt;ID&gt;.nub</c>
/// (Nintendo DSP-ADPCM, decoded by vgmstream). The later discs (みんなでパーティ☆3代目!, 決定版, 超ごうか版) keep their
/// charts in <c>sheet/newsht/solo</c> and <c>duet</c> as arcade fumens (big-endian, like the Wii U's); the release comes
/// from the game id in <c>sys/boot.bin</c>. An ura is a song of its own (<c>ex…</c>, its title ending in （裏）), as on
/// the disc. The WII library (<see cref="SongSourceKind.Wii"/>): ids
/// overlap the arcade's (anp, natsu), and its scores count as custom songs'.
/// </summary>
public sealed partial class WiiCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumMeasures = 100_000;
    private const int MaximumNotes = 1_000_000;
    private const string Courses = "enhm";

    private readonly string _root;

    /// <summary>The disc's files folder it reads.</summary>
    public string Root => _root;
    // Charts in sheet/newsht (arcade fumens) rather than sheet/sht (the 2008 format).
    private readonly bool _fumen;
    private Dictionary<string, WiiChartReader.CourseTuning> _tuning = [];

    /// <param name="filesRoot">The disc's <c>files</c> folder (see <see cref="FindRoot"/>).</param>
    public WiiCatalogProvider(string filesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesRoot));
        _fumen = Directory.Exists(Path.Combine(_root, "sheet", "newsht", "solo"));
        (var prefix, Release, Order) = gameId(_root) switch
        {
            null or "R2JJ" => ("wii1", "Wii", 1),
            "S2TJ" => ("wii2", "Wii 2", 2),
            "S3TJ" => ("wii3", "Wii 3", 3),
            "STJJ" => ("wii4", "Wii Ketteiban", 4),
            "S5KJ" => ("wii5", "Wii Chou Goukaban", 5),
            var other => ("wii-" + other.ToLowerInvariant(), "Wii " + other, 6),
        };
        Id = new CatalogProviderId(prefix);
    }

    public CatalogProviderId Id { get; }

    /// <summary>The release, as a song's title names it when the releases chart it differently.</summary>
    public string Release { get; }

    /// <summary>Release order (higher is newer).</summary>
    public int Order { get; }

    // The disc's 4-letter game id (sys/boot.bin beside files), null when unreadable.
    private static string? gameId(string root)
    {
        var path = Path.Combine(Path.GetDirectoryName(root) ?? root, "sys", "boot.bin");
        if (!File.Exists(path))
            return null;
        Span<byte> id = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(id) == 4 && GameId().IsMatch(System.Text.Encoding.ASCII.GetString(id)) ? System.Text.Encoding.ASCII.GetString(id) : null;
    }

    public SongSourceKind Source => SongSourceKind.Wii;

    /// <summary>
    /// The disc's <c>files</c> folder in what a player picked: the folder itself, its <c>files</c>, or an extraction's
    /// game partition (<c>game-1/files</c> from wit/nkit, <c>DATA/files</c> from Dolphin); null when none is a Wii disc.
    /// </summary>
    public static string? FindRoot(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new[] { folder, Path.Combine(folder, "files"), Path.Combine(folder, "game-1", "files"), Path.Combine(folder, "DATA", "files") }
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .FirstOrDefault(static path => File.Exists(musicInfoPath(path))
                && (Directory.Exists(Path.Combine(path, "sheet", "sht", "bin")) || Directory.Exists(Path.Combine(path, "sheet", "newsht", "solo"))));
    }

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CatalogDiagnostic>();
        var entries = WiiChartReader.ReadMusicInfo(read(musicInfoPath(_root)));
        var tuningPath = Path.Combine(_root, "sheet", "tuning", "bin", "tuning.bin");
        _tuning = File.Exists(tuningPath) ? WiiChartReader.ReadTuning(read(tuningPath)) : [];
        var songs = new List<SongDescriptor>();
        var genres = new SortedDictionary<int, List<SongKey>>();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogScanProgress(Id, "inspect", index, entries.Count));
            var entry = entries[index];
            if (!MusicId().IsMatch(entry.Id))
                continue;
            var key = new SongKey(Source, $"{Id.Value}:{entry.Id}");
            var charts = Enumerable.Range(0, Courses.Length)
                .Where(course => File.Exists(chartPath(entry.Id, Courses[course])))
                .Select(course => new SongChartDescriptor(new ChartKey(key, ((TaikoCourse)course).ToString().ToLowerInvariant()),
                    ((TaikoCourse)course).ToString(), new CatalogAssetKey(Id, $"chart:{entry.Id}:{Courses[course]}"),
                    (TaikoCourse)course, stars(entry.Id, Courses[course]),
                    _fumen && File.Exists(duetPath(entry.Id, Courses[course], "1")) && File.Exists(duetPath(entry.Id, Courses[course], "2"))
                        ? [new CatalogAssetKey(Id, $"duet:{entry.Id}:{Courses[course]}:1"), new CatalogAssetKey(Id, $"duet:{entry.Id}:{Courses[course]}:2")]
                        : null))
                .ToArray();
            if (charts.Length == 0)
                continue;
            var hasAudio = File.Exists(audioPath(entry.Id));
            if (!hasAudio)
                diagnostics.Add(new(CatalogDiagnosticSeverity.Warning, "WII_AUDIO_MISSING", $"Wii song '{entry.Id}' has no music.", Id));
            songs.Add(new SongDescriptor(key, new SongTitle(entry.Title, entry.Title), null, charts,
                hasAudio ? new CatalogAssetKey(Id, $"audio:{entry.Id}") : null, hasAudio ? entry.Preview : null)
            {
                ScenePresets = Skins.TryGetValue(entry.Id, out var skin) ? [skin] : [],
            });
            (genres.TryGetValue(entry.Genre, out var list) ? list : genres[entry.Genre] = []).Add(key);
        }
        progress?.Report(new CatalogScanProgress(Id, "complete", entries.Count, entries.Count));
        return ValueTask.FromResult(new SongCatalogContribution(Id, Source, songs,
            // The game's own genre art for each folder; the name says which release.
            genres.Select((genre, index) => new SongCategoryDescriptor(new CategoryKey(Source, $"{Id.Value}-{genre.Key}"),
                Release + " " + genreName(genre.Key).English, 10 * Order + index, genre.Value)
                {
                    Genre = genreName(genre.Key).Japanese,
                    Colour = WiiCatalogProvider.GenreColour(genreName(genre.Key).Japanese),
                }),
            diagnostics));
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id)
            throw new ArgumentException("The asset belongs to a different catalog provider.", nameof(asset));
        return ValueTask.FromResult<Stream>(new FileStream(assetPath(asset), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id || !(asset.StableId.StartsWith("chart:", StringComparison.Ordinal) || asset.StableId.StartsWith("duet:", StringComparison.Ordinal)))
            throw new ArgumentException("The asset is not a Wii chart of this provider.", nameof(asset));
        var path = assetPath(asset);
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The Wii chart exceeds the chart-size limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var fields = asset.StableId.Split(':');
        if (_fumen)
            return FumenChartReader.Read(WiiChartReader.CompactToFumen(bytes, MaximumMeasures) ?? bytes, chart, MaximumMeasures, MaximumNotes, wiiNotes: true)
                with { Level = stars(fields[1], fields[2][0]) };
        var fumen = WiiChartReader.ToFumen(bytes, _tuning.GetValueOrDefault(courseName(fields[1], fields[2][0])), MaximumMeasures, MaximumNotes);
        return FumenChartReader.Read(fumen, chart, MaximumMeasures, MaximumNotes, wiiNotes: true) with { Level = stars(fields[1], fields[2][0]) };
    }

    // "chart:<id>:<course letter>", "duet:<id>:<course>:<1|2>" or "audio:<id>"; ids are validated so a key cannot leave the root.
    private string assetPath(CatalogAssetKey asset) => asset.StableId.Split(':') switch
    {
        ["chart", var id, [var course]] when MusicId().IsMatch(id) && Courses.Contains(course) => chartPath(id, course),
        ["duet", var id, [var course], "1" or "2"] when _fumen && MusicId().IsMatch(id) && Courses.Contains(course) => duetPath(id, course, asset.StableId[^1..]),
        ["audio", var id] when MusicId().IsMatch(id) => audioPath(id),
        _ => throw new ArgumentException("The Wii asset key is malformed.", nameof(asset)),
    };

    private int? stars(string id, char course) =>
        _tuning.GetValueOrDefault(courseName(id, course)) is { Stars: > 0 and <= 10 } tuning ? tuning.Stars : null;

    private static string courseName(string id, char course) => $"{id}1p_{course}";

    private string chartPath(string id, char course) => _fumen
        ? Path.Combine(_root, "sheet", "newsht", "solo", $"{id}_{course}.bin")
        : Path.Combine(_root, "sheet", "sht", "bin", courseName(id, course) + ".bin");

    private string duetPath(string id, char course, string player) => Path.Combine(_root, "sheet", "newsht", "duet", $"{id}_{course}_{player}.bin");

    // A G.719 bank arrives from another device as Opus (Waddamburo.App's WiiAudio): SONG_<ID>.ogg beside the rest.
    private string audioPath(string id)
    {
        var bank = Path.Combine(_root, "sound", $"SONG_{id.ToUpperInvariant()}.nub");
        var converted = Path.ChangeExtension(bank, ".ogg");
        return !File.Exists(bank) && File.Exists(converted) ? converted : bank;
    }

    private static string musicInfoPath(string root) => Path.Combine(root, "sheet", "musicinfo", "bin", "musicinfo.bin");

    private static byte[] read(string path) => new FileInfo(path).Length <= MaximumFileBytes
        ? File.ReadAllBytes(path) : throw new InvalidDataException($"{Path.GetFileName(path)} is too large.");

    private static (string English, string? Japanese) genreName(int genre) => genre switch
    {
        0 => ("J-POP", "J-POP"),
        1 => ("Anime", "アニメ"),
        2 => ("Variety", "バラエティ"),
        3 => ("Classical", "クラシック"),
        4 => ("Namco Original", "ナムコオリジナル"),
        5 => ("Kids", "童謡"),
        6 => ("Game Music", "ゲームミュージック"),
        _ => ($"Genre {genre}", null),
    };

    /// <summary>A genre's folder colour (a folder sharing its genre with another is recoloured with it).</summary>
    internal static uint? GenreColour(string? genre) => genre switch
    {
        "J-POP" => 0x42C0D2,
        "アニメ" => 0xFF9A00,
        "ボーカロイド" => 0xB4B4B4,
        "童謡" => 0xFF5BA7,
        "バラエティ" => 0x8ECE26,
        "クラシック" => 0xCAC101,
        "ゲームミュージック" => 0x9369C7,
        "ナムコオリジナル" => 0xFF5A30,
        _ => null,
    };

    // The arcade's gameplay skin (enso_<name>) a Wii song plays with: the ones with a matching theme.
    private static readonly Dictionary<string, string> Skins = new(StringComparer.Ordinal) { ["mario2"] = "mario" };

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex MusicId();
    [GeneratedRegex("^[A-Z0-9]{4}$")]
    private static partial Regex GameId();
}
