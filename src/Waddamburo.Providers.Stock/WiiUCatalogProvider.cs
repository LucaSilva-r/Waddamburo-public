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
/// Any Wii U release with this layout (Wii Uば～じょん!, 特盛り!, あつめて★ともだち大作戦!), told apart by the title id in
/// <c>code/app.xml</c>; the title's update (<c>0005000E…</c>, its files win) and DLC (<c>0005000C…</c>, one
/// <c>content/&lt;nnnn&gt;</c> folder per song with its own <c>musicInfo.drp</c>) join it when found beside the game folder
/// (an extracted set's sibling folders, or Cemu's <c>mlc01/usr/title/&lt;type&gt;/&lt;id&gt;</c>).
/// In the WII U library (<see cref="WiiLibraryProvider"/>, <see cref="SongSourceKind.WiiU"/>).
/// </summary>
public sealed partial class WiiUCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumMeasures = 100_000;
    private const int MaximumNotes = 1_000_000;
    private const string Courses = "enhmx";

    private readonly string _root;
    // The update's content folder (its files replace the game's), null without one.
    private readonly string? _update;
    // The DLC's content folder, null without one.
    private readonly string? _dlc;
    private Dictionary<string, Entry> _entries = [];

    /// <param name="contentRoot">The disc's <c>content</c> folder (see <see cref="FindRoot"/>).</param>
    public WiiUCatalogProvider(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        var title = TitleId(Path.GetDirectoryName(_root)!);
        (var prefix, Release, Order) = (title?[8..]) switch
        {
            null or "10132200" => ("wiiu", "Wii U", 10),
            "10192000" => ("wiiu2", "Wii U Tokumori", 11),
            "101D3000" => ("wiiu3", "Wii U Atsumete", 12),
            var other => ("wiiu-" + other.ToLowerInvariant(), "Wii U " + other, 13),
        };
        Id = new CatalogProviderId(prefix);
        if (title is not null)
        {
            _update = addOn(title, "0005000E");
            _dlc = addOn(title, "0005000C");
        }
    }

    public CatalogProviderId Id { get; }

    /// <summary>The release, as a song's title names it when the releases chart it differently.</summary>
    public string Release { get; }

    /// <summary>Release order (higher is newer; every Wii U release is newer than the Wii ones).</summary>
    public int Order { get; }

    /// <summary>The 16-hex-digit title id in a title folder's <c>code/app.xml</c>, upper-case; null when none.</summary>
    internal static string? TitleId(string titleFolder)
    {
        var path = Path.Combine(titleFolder, "code", "app.xml");
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            return null;
        var match = TitleIdText().Match(File.ReadAllText(path));
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    // The update or DLC (type 0005000E/0005000C) of this title: a sibling of the game folder with that title id, or
    // Cemu's mlc01/usr/title/<type>/<id>. Its content folder, null when none.
    private string? addOn(string title, string type)
    {
        var game = Path.GetDirectoryName(_root)!;
        var parent = Path.GetDirectoryName(game);
        if (parent is null)
            return null;
        var want = type + title[8..];
        // ponytail: scans every sibling's app.xml; fine for a games folder, index by name if one holds thousands.
        var mlc = Path.Combine(Path.GetDirectoryName(parent) ?? parent, type.ToLowerInvariant(), title[8..].ToLowerInvariant());
        var found = new[] { mlc }.Concat(Directory.EnumerateDirectories(parent))
            .FirstOrDefault(folder => string.Equals(TitleId(folder), want, StringComparison.Ordinal));
        return found is not null && Directory.Exists(Path.Combine(found, "content")) ? Path.Combine(found, "content") : null;
    }

    public SongSourceKind Source => SongSourceKind.WiiU;

    /// <summary>The disc's <c>content</c> folder in what a player picked (itself, <c>content</c> or <c>game/content</c>); null when none.</summary>
    public static string? FindRoot(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new[] { folder, Path.Combine(folder, "content"), Path.Combine(folder, "game", "content") }
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .FirstOrDefault(static path => File.Exists(databasePath(path)) && Directory.Exists(Path.Combine(path, "wiiu", "fumen", "solo"))
                // An update has the same layout; it joins its game (see addOn), it is not a disc.
                && (TitleId(Path.GetDirectoryName(path)!) is not { } title || title.StartsWith("00050000", StringComparison.Ordinal)));
    }

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CatalogDiagnostic>();
        var entries = ReadMusicInfo(DrpEntry(read(_update is not null && File.Exists(databasePath(_update)) ? databasePath(_update) : databasePath(_root)), "musicinfo_db"));
        // Each DLC song folder lists its own songs (an ura can sit in another folder than its song).
        if (_dlc is not null)
            foreach (var folder in Directory.EnumerateDirectories(_dlc).Order(StringComparer.Ordinal))
            {
                var info = Path.Combine(folder, "musicInfo.drp");
                if (!File.Exists(info))
                    continue;
                try
                {
                    entries.AddRange(ReadMusicInfo(DrpEntry(read(info), "musicinfo_db")).Select(entry => entry with { Dlc = folder }));
                }
                catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException)
                {
                    diagnostics.Add(new(CatalogDiagnosticSeverity.Warning, "WIIU_DLC_UNREADABLE", $"Wii U DLC '{Path.GetFileName(folder)}': {exception.Message}", Id));
                }
            }
        // The game's entry wins over a DLC one with the same id.
        _entries = entries.GroupBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var songs = new List<SongDescriptor>();
        var genres = new SortedDictionary<int, List<SongKey>>();
        var listed = _entries.Values.Where(static entry => !entry.Id.StartsWith("ex_", StringComparison.Ordinal)).OrderBy(static entry => entry.Order).ToList();
        for (var index = 0; index < listed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogScanProgress(Id, "inspect", index, listed.Count));
            var entry = listed[index];
            if (!MusicId().IsMatch(entry.Id))
                continue;
            var key = new SongKey(Source, $"{Id.Value}:{entry.Id}");
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
            var hasAudio = BankName().IsMatch(entry.Bank) && File.Exists(audioPath(entry.Id));
            if (!hasAudio)
                diagnostics.Add(new(CatalogDiagnosticSeverity.Warning, "WIIU_AUDIO_MISSING", $"Wii U song '{entry.Id}' has no music.", Id));
            songs.Add(new SongDescriptor(key, new SongTitle(entry.Title, entry.Title), null, charts,
                hasAudio ? new CatalogAssetKey(Id, $"audio:{entry.Id}") : null, hasAudio ? preview(charts) : null)
            {
                ScenePresets = Skins.TryGetValue(entry.Id, out var skin) ? [skin] : [],
            });
            (genres.TryGetValue(entry.Genre, out var list) ? list : genres[entry.Genre] = []).Add(key);
        }
        progress?.Report(new CatalogScanProgress(Id, "complete", listed.Count, listed.Count));
        return ValueTask.FromResult(new SongCatalogContribution(Id, Source, songs,
            genres.Select((genre, index) => new SongCategoryDescriptor(new CategoryKey(Source, $"{Id.Value}-{genre.Key}"),
                Release + " " + genreName(genre.Key).English, 100 * (Order - 9) + index, genre.Value)
                {
                    Genre = genreName(genre.Key).Japanese,
                    Colour = WiiCatalogProvider.GenreColour(genreName(genre.Key).Japanese),
                }),
            diagnostics));
    }

    // The song select's preview: the Wii U data names none, so the first Go-Go of the hardest regular chart (where the
    // Wii discs' musicinfo previews mostly sit).
    // ponytail: a third of the Wii discs' previews are within 0.3 s of it, others start a phrase earlier; take the Wii
    // disc's own preview for songs both have if that matters.
    private TimeSpan? preview(SongChartDescriptor[] charts)
    {
        if (charts.Where(static chart => chart.Course != TaikoCourse.Ura).LastOrDefault() is not { } chart)
            return null;
        try
        {
            var path = assetPath(chart.ChartAsset);
            if (new FileInfo(path).Length > MaximumFileBytes)
                return null;
            var played = FumenChartReader.Read(File.ReadAllBytes(path), chart.Key, MaximumMeasures, MaximumNotes, wiiNotes: true);
            return played.EffectPoints.FirstOrDefault(static point => point.IsGoGo) is { IsGoGo: true } gogo ? gogo.Time : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }
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
        return FumenChartReader.Read(bytes, chart, MaximumMeasures, MaximumNotes, wiiNotes: true) with { Level = stars(fields[1], fields[2][0]) };
    }

    // "chart:<id>:<course>", "duet:<id>:<course>:<1|2>" or "audio:<id>"; validated so a key cannot leave the root.
    private string assetPath(CatalogAssetKey asset) => asset.StableId.Split(':') switch
    {
        ["chart", var id, [var course]] when MusicId().IsMatch(id) && Courses.Contains(course) => chartPath(id, course),
        ["duet", var id, [var course], "1" or "2"] when MusicId().IsMatch(id) && Courses.Contains(course)
            => duetPath(id, course, asset.StableId[^1..]),
        ["audio", var id] when MusicId().IsMatch(id) => audioPath(id),
        _ => throw new ArgumentException("The Wii U asset key is malformed.", nameof(asset)),
    };

    // Ura (x) is the ex_ entry's Oni.
    private int? stars(string id, char course) => course == 'x'
        ? _entries.GetValueOrDefault("ex_" + id)?.Stars[3]
        : _entries.GetValueOrDefault(id)?.Stars[Courses.IndexOf(course)];

    private string chartPath(string id, char course) => course == 'x'
        ? file("ex_" + id, "fumen", "solo", $"ex_{id}_m.bin")
        : file(id, "fumen", "solo", $"{id}_{course}.bin");

    private string duetPath(string id, char course, string player) => course == 'x'
        ? file("ex_" + id, "fumen", "duet", $"ex_{id}_m_{player}.bin")
        : file(id, "fumen", "duet", $"{id}_{course}_{player}.bin");

    private string audioPath(string id) => _entries.GetValueOrDefault(id) is { } entry
        ? entry.Dlc is { } folder ? Path.Combine(folder, entry.Bank + ".nus3bank") : file(id, "sound", entry.Bank + ".nus3bank")
        : throw new ArgumentException($"The Wii U song '{id}' is not on this disc.");

    // A song's file under wiiu/ (the update's copy first) or, for a DLC song, its own folder.
    private string file(string id, params string[] parts)
    {
        if (_entries.GetValueOrDefault(id)?.Dlc is { } folder)
            return Path.Combine([folder, .. parts]);
        var path = Path.Combine([_root, "wiiu", .. parts]);
        return _update is not null && Path.Combine([_update, "wiiu", .. parts]) is var updated && File.Exists(updated) ? updated : path;
    }

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

    // Dlc: the DLC folder the song comes from; null for the game's own.
    internal sealed record Entry(string Id, string Title, string Bank, int Genre, int Order, int?[] Stars, bool[] Branches, string? Dlc = null);

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
    [GeneratedRegex("<title_id[^>]*>\\s*([0-9A-Fa-f]{16})\\s*</title_id>")]
    private static partial Regex TitleIdText();
}
