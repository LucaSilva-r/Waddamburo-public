using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// The songs of a 太鼓の達人 Wii Uば～じょん! disc the player extracted (its <c>content</c> folder): the song list from
/// <c>Common/database/db_pack.drp</c>'s <c>musicinfo_db</c> (XML), charts from <c>wiiu/fumen/solo</c> and <c>duet</c>
/// (the arcade fumen, big-endian), music from <c>wiiu/sound/SONG_&lt;ID&gt;.nus3bank</c> (DSP-ADPCM, vgmstream).
/// A <c>ex_&lt;id&gt;</c> entry is the song's ura: its Oni chart is the base song's Ura course.
/// In the WII/WII U library with 太鼓の達人Wii's songs (<see cref="WiiCatalogProvider"/>).
/// </summary>
public sealed partial class WiiUCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumMeasures = 100_000;
    private const int MaximumNotes = 1_000_000;
    private const string Courses = "enhmx";

    private readonly string _root;
    private Dictionary<string, int?[]> _stars = [];

    /// <param name="contentRoot">The disc's <c>content</c> folder (see <see cref="FindRoot"/>).</param>
    public WiiUCatalogProvider(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        Id = new CatalogProviderId("wiiu");
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.Console;

    /// <summary>The disc's <c>content</c> folder in what a player picked (itself, <c>content</c> or <c>game/content</c>); null when none.</summary>
    public static string? FindRoot(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new[] { folder, Path.Combine(folder, "content"), Path.Combine(folder, "game", "content") }
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .FirstOrDefault(static path => File.Exists(databasePath(path)) && Directory.Exists(Path.Combine(path, "wiiu", "fumen", "solo")));
    }

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CatalogDiagnostic>();
        var entries = ReadMusicInfo(DrpEntry(read(databasePath(_root)), "musicinfo_db"));
        var ura = entries.Where(static entry => entry.Id.StartsWith("ex_", StringComparison.Ordinal))
            .ToDictionary(static entry => entry.Id[3..], StringComparer.Ordinal);
        _stars = entries.ToDictionary(static entry => entry.Id, static entry => entry.Stars, StringComparer.Ordinal);
        var songs = new List<SongDescriptor>();
        var genres = new SortedDictionary<int, List<SongKey>>();
        var listed = entries.Where(static entry => !entry.Id.StartsWith("ex_", StringComparison.Ordinal)).OrderBy(static entry => entry.Order).ToList();
        for (var index = 0; index < listed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogScanProgress(Id, "inspect", index, listed.Count));
            var entry = listed[index];
            if (!MusicId().IsMatch(entry.Id))
                continue;
            var key = new SongKey(Source, "wiiu:" + entry.Id);
            var charts = Enumerable.Range(0, Courses.Length)
                .Where(course => File.Exists(chartPath(entry.Id, Courses[course])))
                .Select(course => new SongChartDescriptor(new ChartKey(key, ((TaikoCourse)course).ToString().ToLowerInvariant()),
                    ((TaikoCourse)course).ToString(), new CatalogAssetKey(Id, $"chart:{entry.Id}:{Courses[course]}"),
                    (TaikoCourse)course, stars(entry.Id, Courses[course]),
                    File.Exists(duetPath(entry.Id, Courses[course], "1")) && File.Exists(duetPath(entry.Id, Courses[course], "2"))
                        ? [new CatalogAssetKey(Id, $"duet:{entry.Id}:{Courses[course]}:1"), new CatalogAssetKey(Id, $"duet:{entry.Id}:{Courses[course]}:2")]
                        : null)
                {
                    HasBranches = entry.Branches[Math.Min(course, 3)],
                })
                .ToArray();
            if (charts.Length == 0)
                continue;
            var hasAudio = File.Exists(audioPath(entry.Bank));
            if (!hasAudio)
                diagnostics.Add(new(CatalogDiagnosticSeverity.Warning, "WIIU_AUDIO_MISSING", $"Wii U song '{entry.Id}' has no music.", Id));
            songs.Add(new SongDescriptor(key, new SongTitle(entry.Title, entry.Title), null, charts,
                hasAudio ? new CatalogAssetKey(Id, $"audio:{entry.Bank}") : null)
            {
                ScenePresets = Skins.TryGetValue(entry.Id, out var skin) ? [skin] : [],
            });
            (genres.TryGetValue(entry.Genre, out var list) ? list : genres[entry.Genre] = []).Add(key);
        }
        progress?.Report(new CatalogScanProgress(Id, "complete", listed.Count, listed.Count));
        return ValueTask.FromResult(new SongCatalogContribution(Id, Source, songs,
            genres.Select((genre, index) => new SongCategoryDescriptor(new CategoryKey(Source, $"wiiu-{genre.Key}"),
                "Wii U " + genreName(genre.Key).English, 100 + index, genre.Value)
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
            throw new ArgumentException("The asset is not a Wii U chart of this provider.", nameof(asset));
        var path = assetPath(asset);
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The Wii U chart exceeds the chart-size limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var fields = asset.StableId.Split(':');
        return FumenChartReader.Read(bytes, chart, MaximumMeasures, MaximumNotes, consoleNotes: true) with { Level = stars(fields[1], fields[2][0]) };
    }

    // "chart:<id>:<course>", "duet:<id>:<course>:<1|2>" or "audio:<bank>"; validated so a key cannot leave the root.
    private string assetPath(CatalogAssetKey asset) => asset.StableId.Split(':') switch
    {
        ["chart", var id, [var course]] when MusicId().IsMatch(id) && Courses.Contains(course) => chartPath(id, course),
        ["duet", var id, [var course], "1" or "2"] when MusicId().IsMatch(id) && Courses.Contains(course)
            => duetPath(id, course, asset.StableId[^1..]),
        ["audio", var bank] when BankName().IsMatch(bank) => audioPath(bank),
        _ => throw new ArgumentException("The Wii U asset key is malformed.", nameof(asset)),
    };

    // Ura (x) is the ex_ entry's Oni.
    private int? stars(string id, char course) => course == 'x'
        ? _stars.GetValueOrDefault("ex_" + id)?[3]
        : _stars.GetValueOrDefault(id)?[Courses.IndexOf(course)];

    private string chartPath(string id, char course) => course == 'x'
        ? Path.Combine(_root, "wiiu", "fumen", "solo", $"ex_{id}_m.bin")
        : Path.Combine(_root, "wiiu", "fumen", "solo", $"{id}_{course}.bin");

    private string duetPath(string id, char course, string player) => course == 'x'
        ? Path.Combine(_root, "wiiu", "fumen", "duet", $"ex_{id}_m_{player}.bin")
        : Path.Combine(_root, "wiiu", "fumen", "duet", $"{id}_{course}_{player}.bin");

    private string audioPath(string bank) => Path.Combine(_root, "wiiu", "sound", bank + ".nus3bank");

    private static string databasePath(string root) => Path.Combine(root, "Common", "database", "db_pack.drp");

    private static byte[] read(string path) => new FileInfo(path).Length <= MaximumFileBytes
        ? File.ReadAllBytes(path) : throw new InvalidDataException($"{Path.GetFileName(path)} is too large.");

    /// <summary>
    /// One file of a DRP pack: a 64-byte name, header words, its uncompressed size (big-endian) at +0x60, then
    /// its zlib stream at +0x64.
    /// </summary>
    internal static byte[] DrpEntry(ReadOnlySpan<byte> drp, string name)
    {
        var at = drp.IndexOf((ReadOnlySpan<byte>)System.Text.Encoding.ASCII.GetBytes(name + "\0"));
        if (at < 0 || at > drp.Length - 0x64)
            throw new InvalidDataException($"The DRP pack has no '{name}'.");
        var size = BinaryPrimitives.ReadInt32BigEndian(drp[(at + 0x60)..]);
        if (size is <= 0 or > (int)MaximumFileBytes)
            throw new InvalidDataException($"The DRP entry '{name}' has an invalid size.");
        using var input = new ZLibStream(new MemoryStream(drp[(at + 0x64)..].ToArray()), CompressionMode.Decompress);
        var output = new byte[size];
        input.ReadExactly(output);
        return output;
    }

    internal sealed record Entry(string Id, string Title, string Bank, int Genre, int Order, int?[] Stars, bool[] Branches);

    /// <summary>musicinfo_db: <c>DB_DATA/DATA_SET</c> with id, title, songFileName, order, genreNo, star*, branch*.</summary>
    internal static List<Entry> ReadMusicInfo(byte[] xml)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        string text(XElement set, string name) => ((string?)set.Element(name))?.Trim() ?? "";
        int? number(XElement set, string name) => int.TryParse(text(set, name), out var value) && value > 0 ? value : null;
        return [.. XDocument.Load(reader).Descendants("DATA_SET")
            .Where(set => text(set, "id").Length > 0)
            .Select(set => new Entry(text(set, "id"), text(set, "title") is { Length: > 0 } title ? title : text(set, "id"),
                text(set, "songFileName"), number(set, "genreNo") ?? 0, number(set, "order") ?? int.MaxValue,
                [.. StarFields.Select(name => number(set, name) is { } stars && stars <= 10 ? stars : (int?)null)],
                [.. BranchFields.Select(name => text(set, name).Length > 0)]))];
    }

    private static readonly string[] StarFields = ["starEasy", "starNormal", "starHard", "starMania"];
    private static readonly string[] BranchFields = ["branchEasy", "branchNormal", "branchHard", "branchMania"];

    private static (string English, string? Japanese) genreName(int genre) => genre switch
    {
        0 => ("J-POP", "J-POP"),
        1 => ("Anime", "アニメ"),
        2 => ("Vocaloid", "ボーカロイド"),
        3 => ("Kids", "童謡"),
        4 => ("Variety", "バラエティ"),
        5 => ("Classical", "クラシック"),
        6 => ("Game Music", "ゲームミュージック"),
        7 => ("Namco Original", "ナムコオリジナル"),
        _ => ($"Genre {genre}", null),
    };

    // The arcade's gameplay skin (enso_<name>) a Wii U song plays with.
    private static readonly Dictionary<string, string> Skins = new(StringComparer.Ordinal) { ["mario2"] = "mario", ["dq10b"] = "dq10" };

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex MusicId();
    [GeneratedRegex("^[A-Za-z0-9_]{1,40}$")]
    private static partial Regex BankName();
}
