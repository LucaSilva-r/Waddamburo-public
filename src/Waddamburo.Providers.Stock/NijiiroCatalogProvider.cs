using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// A user's Nijiiro installation, read in place: <c>datatable/musicinfo</c> and <c>wordlist</c> (JSON
/// <c>items</c>), charts <c>fumen/&lt;id&gt;/&lt;id&gt;_&lt;e|n|h|m|x&gt;[_1|_2].bin</c> in the arcade fumen
/// layout (either byte order), music <c>sound/song_&lt;id&gt;.nus3bank</c>. Files are plain, gzip, or
/// AES-256-CBC (16-byte IV first, PKCS#7, then maybe gzip); keys are the 64-hex-digit literals in the
/// installation's own <c>Executable/Release/bnusio.dll</c> that decrypt to valid data.
/// </summary>
public sealed partial class NijiiroCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumMeasures = 16_384;
    private const int MaximumNotes = 1_000_000;
    private const string Courses = "enhmx";
    private static readonly string[] StarFields = ["starEasy", "starNormal", "starHard", "starMania", "starUra"];
    // genreNo 0-7, named as the Song Select categories are (their art and colours).
    private static readonly string[] Genres = ["J-POP", "Anime", "Kids", "Vocaloid", "Game Music", "Namco Original", "Variety", "Classical"];

    private readonly string _root;
    private byte[][]? _keys;
    private Dictionary<string, int[]> _stars = [];

    /// <param name="folder">The installation, its <c>Data/x64</c>, or its <c>fumen</c> folder.</param>
    /// <param name="id">The provider id (default "nijiiro").</param>
    public NijiiroCatalogProvider(string folder, CatalogProviderId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetFileName(root) == "fumen")
            root = Path.GetDirectoryName(root)!;
        if (Directory.Exists(Path.Combine(root, "Data", "x64")))
            root = Path.Combine(root, "Data", "x64");
        else if (Directory.Exists(Path.Combine(root, "x64")))
            root = Path.Combine(root, "x64");
        _root = root;
        Id = id ?? new CatalogProviderId("nijiiro");
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.Nijiiro;

    /// <summary>True when the folder resolves to a Nijiiro data folder.</summary>
    public bool Exists => Directory.Exists(Path.Combine(_root, "fumen")) && Directory.Exists(Path.Combine(_root, "sound"));

    /// <summary>
    /// Why the installation cannot be played (null: it can): no data there, or its song table does not
    /// decrypt (the loader's keys are missing or wrong, so no chart or song would either).
    /// </summary>
    public string? Problem
    {
        get
        {
            if (!Exists)
                return "No Nijiiro data there.";
            try
            {
                _ = table("musicinfo");
                return null;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException
                or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            {
                return exception.Message;
            }
        }
    }

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new CatalogScanProgress(Id, "discover", 0, null));
        var diagnostics = new List<CatalogDiagnostic>();
        var music = table("musicinfo");
        var titles = new Dictionary<string, (string Japanese, string English)>(StringComparer.Ordinal);
        foreach (var entry in table("wordlist"))
        {
            var japanese = text(entry, "japaneseText");
            var english = text(entry, "englishUsText");
            // Megamix repeats keys, later ones blank or holding a subtitle: the first usable one wins.
            if ((japanese ?? english) is { } any && text(entry, "key") is { } key)
                titles.TryAdd(key, (japanese ?? any, english ?? any));
        }

        var songs = new List<SongDescriptor>();
        var categories = new SortedDictionary<int, List<SongKey>>();
        var stars = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < music.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = music[index];
            if (text(entry, "id") is not { } id || !SongId().IsMatch(id) || !seen.Add(id))
                continue;
            var audio = audioPath(id);
            if (!File.Exists(audio))
            {
                diagnostics.Add(warning("NIJIIRO_AUDIO_MISSING", $"Song '{id}' has no song_{id}.nus3bank."));
                continue;
            }
            var key = new SongKey(Source, id);
            var levels = new int[Courses.Length];
            var charts = new List<SongChartDescriptor>();
            for (var course = 0; course < Courses.Length; course++)
            {
                var letter = Courses[course];
                if (!File.Exists(chartPath(id, letter, "")))
                    continue;
                levels[course] = entry.TryGetProperty(StarFields[course], out var star) && star.TryGetInt32(out var value)
                    && value is > 0 and <= 10 ? value : 0;
                // A missing player file falls back to the solo chart (as the game does).
                charts.Add(new SongChartDescriptor(
                    new ChartKey(key, ((TaikoCourse)course).ToString().ToLowerInvariant()),
                    ((TaikoCourse)course).ToString(),
                    new CatalogAssetKey(Id, $"chart:{id}:{letter}"),
                    (TaikoCourse)course,
                    levels[course] > 0 ? levels[course] : null,
                    [new CatalogAssetKey(Id, $"chart:{id}:{letter}:1"), new CatalogAssetKey(Id, $"chart:{id}:{letter}:2")]));
            }
            if (charts.Count == 0)
                continue;
            stars[id] = levels;
            var title = titles.GetValueOrDefault("song_" + id);
            var subtitle = titles.GetValueOrDefault("song_sub_" + id);
            songs.Add(new SongDescriptor(
                key,
                new SongTitle(title.Japanese ?? id, title.Japanese, title.English),
                null,
                charts,
                new CatalogAssetKey(Id, $"audio:{id}"),
                previewStart(audio),
                subtitle.Japanese)
            {
                EnglishSubtitle = subtitle.English,
            });
            var genre = entry.TryGetProperty("genreNo", out var number) && number.TryGetInt32(out var genreNo)
                && genreNo >= 0 && genreNo < Genres.Length ? genreNo : Genres.Length;
            if (!categories.TryGetValue(genre, out var list))
                categories[genre] = list = [];
            list.Add(key);
        }
        _stars = stars;
        progress?.Report(new CatalogScanProgress(Id, "complete", music.Count, music.Count));
        return ValueTask.FromResult(new SongCatalogContribution(
            Id,
            Source,
            songs,
            categories.Select((pair, order) => new SongCategoryDescriptor(
                new CategoryKey(Source, pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                pair.Key < Genres.Length ? Genres[pair.Key] : "Other",
                order,
                pair.Value)),
            diagnostics));
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id || asset.StableId.Split(':') is not ["audio", var id] || !SongId().IsMatch(id))
            throw new ArgumentException("The asset is not Nijiiro music of this provider.", nameof(asset));
        // A FileStream: the decoder hands its path to vgmstream-cli for G.719 banks.
        Stream stream = new FileStream(audioPath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public async ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id || asset.StableId.Split(':') is not ["chart", var id, [var letter], .. var player]
            || !SongId().IsMatch(id) || !Courses.Contains(letter, StringComparison.Ordinal)
            || player is not ([] or ["1" or "2"]))
            throw new ArgumentException("The asset is not a Nijiiro chart of this provider.", nameof(asset));
        var path = chartPath(id, letter, player is [var side] ? "_" + side : "");
        if (!File.Exists(path))
            path = chartPath(id, letter, "");
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The selected fumen exceeds the chart-size limit.");
        var bytes = unpack(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), table: false);
        var course = Courses.IndexOf(letter, StringComparison.Ordinal);
        return FumenChartReader.Read(bytes, chart, MaximumMeasures, MaximumNotes) with
        {
            Level = _stars.TryGetValue(id, out var levels) && levels[course] > 0 ? levels[course] : null,
        };
    }

    private string chartPath(string id, char course, string player) =>
        Path.Combine(_root, "fumen", id, $"{id}_{course}{player}.bin");

    private string audioPath(string id) => Path.Combine(_root, "sound", $"song_{id}.nus3bank");

    // datatable/<name>.json, or the packed .bin; its "items" array.
    private List<JsonElement> table(string name)
    {
        var path = Path.Combine(_root, "datatable", name + ".json");
        if (!File.Exists(path))
            path = Path.ChangeExtension(path, ".bin");
        using var document = JsonDocument.Parse(unpack(File.ReadAllBytes(path), table: true));
        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.Clone())];
    }

    private byte[] unpack(byte[] data, bool table)
    {
        if (isGzip(data))
            return inflate(data);
        if (plausible(data, table))
            return data;
        if (data.Length < 32 || data.Length % 16 != 0)
            throw new InvalidDataException("The Nijiiro file is neither plain, gzip nor AES-encrypted.");
        foreach (var key in _keys ??= loaderKeys())
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] plain;
            try
            {
                plain = aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
            }
            catch (CryptographicException)
            {
                continue; // wrong key: the padding does not check out
            }
            if (isGzip(plain))
                return inflate(plain);
            if (plausible(plain, table))
                return plain;
        }
        throw new InvalidDataException("A Nijiiro file cannot be decrypted: the installation's Executable/Release/bnusio.dll is needed.");
    }

    // Key candidates: every 64-hex-digit literal in the installation's loader (never executed).
    private byte[][] loaderKeys()
    {
        var loader = Path.Combine(_root, "..", "..", "Executable", "Release", "bnusio.dll");
        if (!File.Exists(loader))
            return [];
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(loader));
        return [.. HexKey().Matches(text).Select(static match => match.Value).Distinct().Take(128).Select(Convert.FromHexString)];
    }

    private static bool isGzip(byte[] data) => data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b;

    private static byte[] inflate(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaximumFileBytes)
                throw new InvalidDataException("A decompressed Nijiiro file exceeds 16 MiB.");
        }
        return output.ToArray();
    }

    // A table is JSON (an object); a fumen has its 12345678 marker at 508 or a sane measure count at 512.
    private static bool plausible(byte[] data, bool table)
    {
        if (table)
        {
            var text = data.AsSpan(data.AsSpan().StartsWith("\xef\xbb\xbf"u8) ? 3 : 0);
            var first = text.IndexOfAnyExcept(" \r\n\t"u8);
            return first >= 0 && text[first] == (byte)'{';
        }
        if (data.Length < 520)
            return false;
        var span = data.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[508..]) == 12345678 || BinaryPrimitives.ReadUInt32BigEndian(span[508..]) == 12345678)
            return true;
        return BinaryPrimitives.ReadUInt32LittleEndian(span[512..]) is > 0 and <= MaximumMeasures
            || BinaryPrimitives.ReadUInt32BigEndian(span[512..]) is > 0 and <= MaximumMeasures;
    }

    // The 39.06 single-song bank template keeps its preview cue (ms) at 0x6C4; other layouts start at 0.
    private static TimeSpan? previewStart(string bank)
    {
        try
        {
            var header = new byte[0x750];
            using (var stream = File.OpenRead(bank))
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                    return null;
            var span = header.AsSpan();
            if (!span.StartsWith("NUS3"u8) || !span[0x748..].StartsWith("PACK"u8) || !span[0x5EC..].StartsWith("TONE"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(span[0x5F0..]) != 0x140)
                return null;
            var cue = BinaryPrimitives.ReadUInt32LittleEndian(span[0x6C4..]);
            return cue < 3_600_000 ? TimeSpan.FromMilliseconds(cue) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private CatalogDiagnostic warning(string code, string message) =>
        new(CatalogDiagnosticSeverity.Warning, code, message, Id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SongId();

    [GeneratedRegex("[0-9A-Fa-f]{64}")]
    private static partial Regex HexKey();
}
