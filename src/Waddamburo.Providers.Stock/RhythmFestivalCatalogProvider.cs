using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// A user's Taiko no Tatsujin: Rhythm Festival (Steam) installation, read in place. The song table is
/// <c>StreamingAssets/ReadAssets/musicinfo.bin</c> (JSON <c>items</c>, titles included), joined by the songs the game
/// has from its subscription server (<c>ServerDataCacheSteam/SongdataDetails</c> beside the downloads; musicinfo's entry
/// wins for a song in both). A song's files are its music
/// <c>SONG_&lt;ID&gt;.bin</c> (a CRI ACB) and charts <c>&lt;id&gt;_&lt;e|n|h|m|x&gt;[_1|_2].bin</c> (fumen,
/// little-endian), found loose (<c>sound/</c>, <c>fumen/</c>), in the bundled <c>packedSongs</c> and
/// <c>localPackedSongs</c> (folders or zips), and in the downloaded songs (LocalLow's <c>PackedSongsSteam</c>), as
/// <c>&lt;UniqueId&gt;_trail_&lt;version&gt;.zip</c>: the highest version holding a song wins. Every file is encrypted
/// (<see cref="RhythmFestivalKeys"/>). The music is decrypted once into <c>cacheFolder</c> as <c>.acb</c> for
/// vgmstream-cli, which carries the HCA key.
/// </summary>
public sealed partial class RhythmFestivalCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private const string DataFolder = "Taiko no Tatsujin Rhythm Festival_Data";
    private const int MaximumMeasures = 16_384;
    private const int MaximumNotes = 1_000_000;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const string Courses = "enhmx";
    private static readonly string[] StarFields = ["StarEasy", "StarNormal", "StarHard", "StarMania", "StarUra"];
    private static readonly string[] BranchFields = ["BranchEasy", "BranchNormal", "BranchHard", "BranchMania", "BranchUra"];
    // GenreNo 0-7, named as the Song Select categories are (their art and colours).
    private static readonly string[] Genres = ["J-POP", "Anime", "Vocaloid", "Variety", "Kids", "Classical", "Game Music", "Namco Original"];

    private readonly string _install;
    private readonly string _downloads;
    private readonly string _cache;
    private readonly Lock _gate = new();
    private RhythmFestivalKeys? _keys;
    private Dictionary<string, SongFiles> _files = new(StringComparer.Ordinal);
    private Dictionary<string, int[]> _stars = [];

    /// <param name="folder">The installation (the folder holding <c>GameAssembly.dll</c>), its data folder, or its
    /// <c>StreamingAssets</c>.</param>
    /// <param name="cacheFolder">Where decrypted music is kept (default: the temp folder's).</param>
    /// <param name="downloads">The downloaded songs' folder (default: LocalLow's <c>PackedSongsSteam</c>).</param>
    /// <param name="id">The provider id (default "rhythm-festival").</param>
    public RhythmFestivalCatalogProvider(string folder, string? cacheFolder = null, string? downloads = null, CatalogProviderId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetFileName(root) == "StreamingAssets")
            root = Path.GetDirectoryName(root)!;
        if (Path.GetFileName(root) == DataFolder)
            root = Path.GetDirectoryName(root)!;
        _install = root;
        _downloads = downloads ?? DownloadsFolder(root);
        _cache = cacheFolder ?? Path.Combine(Path.GetTempPath(), "Waddamburo", "rhythm-festival");
        Id = id ?? new CatalogProviderId("rhythm-festival");
    }

    private const string SteamAppId = "2288630";
    private const string InstallName = "Taiko no Tatsujin Rhythm Festival";
    private static readonly string[] SaveFolder = ["AppData", "LocalLow", "Bandai Namco Entertainment Inc_", InstallName, "PackedSongsSteam"];

    /// <summary>
    /// Where the game keeps its downloaded songs: LocalLow on Windows; under Proton, the same path in the game's prefix
    /// (<c>steamapps/compatdata/&lt;app&gt;/pfx/drive_c/users/steamuser</c>) of the library holding the installation.
    /// </summary>
    public static string DownloadsFolder(string install)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine([Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "",
                .. SaveFolder[1..]]);
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(install)) ?? install;
        return Path.Combine([steamapps, "compatdata", SteamAppId, "pfx", "drive_c", "users", "steamuser", .. SaveFolder]);
    }

    /// <summary>
    /// Installations found without being picked, by the game's Steam app id: each Steam library (Steam's own, found
    /// from the registry on Windows, and those its <c>libraryfolders.vdf</c> lists) whose
    /// <c>steamapps/appmanifest_&lt;app&gt;.acf</c> names the game's <c>installdir</c>. Without a manifest, the default
    /// <c>steamapps/common</c> folder is tried, and on Windows <c>&lt;drive&gt;:\SteamLibrary</c> on each drive from C:.
    /// </summary>
    public static IReadOnlyList<string> FindInstallations()
    {
        var libraries = new List<string>();
        foreach (var steam in SteamFolders())
        {
            libraries.Add(steam);
            libraries.AddRange(VdfValues(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "path"));
        }
        if (OperatingSystem.IsWindows())
            for (var drive = 'C'; drive <= 'Z'; drive++)
                libraries.Add($@"{drive}:\SteamLibrary");
        return [.. libraries
            .Select(static library => Path.Combine(library, "steamapps", "common",
                VdfValues(Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf"), "installdir").FirstOrDefault() ?? InstallName))
            .Where(static install => new RhythmFestivalCatalogProvider(install).Exists)
            .Select(static install => Path.GetFullPath(install))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    // Steam's own folder: the registry's SteamPath on Windows (else Program Files); the native, symlinked and Flatpak
    // locations elsewhere.
    private static IEnumerable<string> SteamFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string { Length: > 0 } path)
                yield return path.Replace('/', '\\');
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield break;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    // A Valve KeyValues file's "<key>"  "<value>" pairs (libraryfolders.vdf's "path", an app manifest's "installdir"),
    // backslashes unescaped; none when it is missing.
    internal static IEnumerable<string> VdfValues(string file, string key)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return Regex.Matches(text, $"\"{Regex.Escape(key)}\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase)
            .Select(static match => match.Groups["value"].Value.Replace(@"\\", @"\"));
    }

    public CatalogProviderId Id { get; }

    /// <summary>The installation read (the folder holding <c>GameAssembly.dll</c>).</summary>
    public string Root => _install;

    public SongSourceKind Source => SongSourceKind.RhythmFestival;

    private string Assets => Path.Combine(_install, DataFolder, "StreamingAssets");

    /// <summary>True when the folder resolves to a Rhythm Festival installation.</summary>
    public bool Exists => File.Exists(Path.Combine(Assets, "ReadAssets", "musicinfo.bin"))
        && File.Exists(Path.Combine(_install, "GameAssembly.dll"));

    /// <summary>
    /// Why the installation cannot be played (null: it can): no installation there, or its binaries yield no cipher
    /// that opens its song table.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (!Exists)
                return "No Rhythm Festival installation there.";
            try
            {
                _ = keys();
                return null;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return exception.Message;
            }
        }
    }

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new CatalogScanProgress(Id, "discover", 0, null));
        var diagnostics = new List<CatalogDiagnostic>();
        var files = discover(diagnostics, cancellationToken);
        var music = table("musicinfo");
        music.AddRange(serverSongs(diagnostics));
        var songs = new List<SongDescriptor>();
        var categories = new SortedDictionary<int, List<SongKey>>();
        var stars = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < music.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogScanProgress(Id, "inspect", index, music.Count));
            var entry = music[index];
            if (text(entry, "Id") is not { } id || !SongId().IsMatch(id) || !seen.Add(id)
                || entry.TryGetProperty("Debug", out var debug) && debug.ValueKind == JsonValueKind.True)
                continue;
            // A song neither installed nor downloaded is not listed.
            if (!files.TryGetValue(id, out var song) || !song.Has(audioName(id)))
                continue;
            var key = new SongKey(Source, id);
            var levels = new int[Courses.Length];
            var charts = new List<SongChartDescriptor>();
            for (var course = 0; course < Courses.Length; course++)
            {
                var letter = Courses[course];
                if (!song.Has(chartName(id, letter, "")))
                    continue;
                levels[course] = entry.TryGetProperty(StarFields[course], out var star) && star.TryGetInt32(out var value)
                    && value is > 0 and <= 10 ? value : 0;
                charts.Add(new SongChartDescriptor(
                    new ChartKey(key, ((TaikoCourse)course).ToString().ToLowerInvariant()),
                    ((TaikoCourse)course).ToString(),
                    new CatalogAssetKey(Id, $"chart:{id}:{letter}"),
                    (TaikoCourse)course,
                    levels[course] > 0 ? levels[course] : null,
                    [new CatalogAssetKey(Id, $"chart:{id}:{letter}:1"), new CatalogAssetKey(Id, $"chart:{id}:{letter}:2")])
                {
                    HasBranches = entry.TryGetProperty(BranchFields[course], out var branch) && branch.ValueKind == JsonValueKind.True,
                });
            }
            if (charts.Count == 0)
                continue;
            stars[id] = levels;
            var japanese = plain(text(entry, "SongNameJP"));
            var english = plain(text(entry, "SongNameEN"));
            songs.Add(new SongDescriptor(
                key,
                new SongTitle(japanese ?? english ?? id, japanese, english),
                null,
                charts,
                new CatalogAssetKey(Id, $"audio:{id}"),
                null,
                plain(text(entry, "SongSubJP")))
            {
                EnglishSubtitle = plain(text(entry, "SongSubEN")),
            });
            var genre = entry.TryGetProperty("GenreNo", out var number) && number.TryGetInt32(out var genreNo)
                && genreNo >= 0 && genreNo < Genres.Length ? genreNo : Genres.Length;
            if (!categories.TryGetValue(genre, out var list))
                categories[genre] = list = [];
            list.Add(key);
        }
        lock (_gate)
        {
            _files = files;
            _stars = stars;
        }
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

    public async ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id || asset.StableId.Split(':') is not ["audio", var id] || !SongId().IsMatch(id))
            throw new ArgumentException("The asset is not Rhythm Festival music of this provider.", nameof(asset));
        var song = files(id);
        var name = audioName(id);
        // Decrypted once per source file; a FileStream, as the decoder hands its path to vgmstream-cli.
        var path = Path.Combine(_cache, $"{id}-{song.Stamp(name)}.acb");
        if (!File.Exists(path))
        {
            var plain = keys().Decrypt(await song.ReadAsync(name, cancellationToken).ConfigureAwait(false), name);
            Directory.CreateDirectory(_cache);
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(partial, plain, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(partial, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(partial); // another load wrote it first
            }
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id || asset.StableId.Split(':') is not ["chart", var id, [var letter], .. var player]
            || !SongId().IsMatch(id) || !Courses.Contains(letter, StringComparison.Ordinal)
            || player is not ([] or ["1" or "2"]))
            throw new ArgumentException("The asset is not a Rhythm Festival chart of this provider.", nameof(asset));
        var song = files(id);
        // A missing player file falls back to the solo chart (as the game does).
        var name = chartName(id, letter, player is [var side] ? "_" + side : "");
        if (!song.Has(name))
            name = chartName(id, letter, "");
        var bytes = keys().Decrypt(await song.ReadAsync(name, cancellationToken).ConfigureAwait(false), name);
        var course = Courses.IndexOf(letter, StringComparison.Ordinal);
        int[]? levels;
        lock (_gate)
            levels = _stars.GetValueOrDefault(id);
        return FumenChartReader.Read(bytes, chart, MaximumMeasures, MaximumNotes) with
        {
            Level = levels is not null && levels[course] > 0 ? levels[course] : null,
        };
    }

    private SongFiles files(string id)
    {
        lock (_gate)
            return _files.TryGetValue(id, out var song) ? song
                : throw new FileNotFoundException($"Rhythm Festival song '{id}' is not installed.");
    }

    private RhythmFestivalKeys keys()
    {
        lock (_gate)
            return _keys ??= RhythmFestivalKeys.Find(
                Path.Combine(_install, DataFolder, "il2cpp_data", "Metadata", "global-metadata.dat"),
                Path.Combine(_install, "GameAssembly.dll"),
                Path.Combine(Assets, "ReadAssets", "musicinfo.bin"));
    }

    private List<JsonElement> table(string name)
    {
        var path = Path.Combine(Assets, "ReadAssets", name + ".bin");
        using var document = JsonDocument.Parse(keys().Decrypt(File.ReadAllBytes(path), name));
        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.Clone())];
    }

    // musicinfo's names for the server song data's fields (genre, stars and branches are numbered alike).
    private static readonly (string Server, string MusicInfo)[] ServerFields =
    [
        ("song_uid", "UniqueId"), ("song_id", "Id"), ("genre_id", "GenreNo"),
        ("song_name_ja", "SongNameJP"), ("song_name_en", "SongNameEN"), ("song_name_sub_ja", "SongSubJP"), ("song_name_sub_en", "SongSubEN"),
        ("star_1", "StarEasy"), ("star_2", "StarNormal"), ("star_3", "StarHard"), ("star_4", "StarMania"), ("star_5", "StarUra"),
        ("is_branch_1", "BranchEasy"), ("is_branch_2", "BranchNormal"), ("is_branch_3", "BranchHard"),
        ("is_branch_4", "BranchMania"), ("is_branch_5", "BranchUra"),
    ];

    // The server's song data as musicinfo items: none when the game has not cached it.
    private List<JsonElement> serverSongs(List<CatalogDiagnostic> diagnostics)
    {
        var path = Path.Combine(Path.GetDirectoryName(_downloads) ?? _downloads, "ServerDataCacheSteam", "SongdataDetails");
        if (!File.Exists(path))
            return [];
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException("SongdataDetails exceeds 64 MiB.");
            using var document = JsonDocument.Parse(RhythmFestivalKeys.DecryptServerCache(File.ReadAllBytes(path)));
            var items = new List<JsonElement>();
            foreach (var song in document.RootElement.GetProperty("ary_song_datail").EnumerateArray())
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var (server, name) in ServerFields)
                        if (song.TryGetProperty(server, out var value))
                        {
                            writer.WritePropertyName(name);
                            value.WriteTo(writer);
                        }
                    writer.WriteEndObject();
                }
                using var item = JsonDocument.Parse(buffer.WrittenMemory);
                items.Add(item.RootElement.Clone());
            }
            return items;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            diagnostics.Add(warning("RHYTHM_FESTIVAL_SERVER_DATA_UNREADABLE", $"The downloaded songs' details could not be read: {exception.Message}"));
            return [];
        }
    }

    // Every song's files: loose ones (version 0), then packed folders and zips, a higher version replacing a lower.
    private Dictionary<string, SongFiles> discover(List<CatalogDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, SongFiles>(StringComparer.Ordinal);
        void offer(SongFiles candidate)
        {
            if (candidate.Id is { } id && (!found.TryGetValue(id, out var current) || candidate.Version > current.Version))
                found[id] = candidate;
        }

        // Loose: sound/SONG_<ID>.bin with fumen/<id>_*.bin.
        var sound = Path.Combine(Assets, "sound");
        var fumen = Path.Combine(Assets, "fumen");
        if (Directory.Exists(sound))
            foreach (var path in Directory.EnumerateFiles(sound, "SONG_*.bin"))
            {
                var id = Path.GetFileNameWithoutExtension(path)["SONG_".Length..].ToLowerInvariant();
                if (SongId().IsMatch(id))
                    offer(SongFiles.Loose(id, sound, fumen));
            }

        foreach (var folder in (string[])[Path.Combine(Assets, "packedSongs"), Path.Combine(Assets, "localPackedSongs"), _downloads])
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (Package().Match(name) is not { Success: true } match)
                    continue;
                var version = int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    offer(Directory.Exists(path) ? SongFiles.Folder(path, version) : SongFiles.Zip(path, version));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    diagnostics.Add(warning("RHYTHM_FESTIVAL_PACKAGE_UNREADABLE", $"{name} could not be read: {exception.Message}"));
                }
            }
        }
        return found;
    }

    private static string audioName(string id) => $"SONG_{id.ToUpperInvariant()}.bin";

    private static string chartName(string id, char course, string player) => $"{id}_{course}{player}.bin";

    // Titles carry the font they are drawn in (<font=jp>...).
    private static string? plain(string? text) =>
        text is null ? null : Tag().Replace(text, "").Trim() is { Length: > 0 } stripped ? stripped : null;

    private static string? text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private CatalogDiagnostic warning(string code, string message) =>
        new(CatalogDiagnosticSeverity.Warning, code, message, Id);

    /// <summary>One song's files: in a folder (loose files split between two), or in a zip.</summary>
    private sealed class SongFiles
    {
        private readonly HashSet<string> _names;
        private readonly Func<string, string> _locate; // file: its path; zip: the archive's
        private readonly bool _zip;

        private SongFiles(string? id, int version, HashSet<string> names, Func<string, string> locate, bool zip)
        {
            Id = id;
            Version = version;
            _names = names;
            _locate = locate;
            _zip = zip;
        }

        public string? Id { get; }

        public int Version { get; }

        public bool Has(string name) => _names.Contains(name);

        public static SongFiles Loose(string id, string sound, string fumen)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { audioName(id) };
            foreach (var letter in Courses)
                foreach (var player in (string[])["", "_1", "_2"])
                    if (File.Exists(Path.Combine(fumen, chartName(id, letter, player))))
                        names.Add(chartName(id, letter, player));
            return new SongFiles(id, 0, names,
                name => Path.Combine(name.StartsWith("SONG_", StringComparison.OrdinalIgnoreCase) ? sound : fumen, name), zip: false);
        }

        public static SongFiles Folder(string folder, int version)
        {
            var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new SongFiles(songOf(names), version, names, name => Path.Combine(folder, name), zip: false);
        }

        public static SongFiles Zip(string path, int version)
        {
            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(static entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new SongFiles(songOf(names), version, names, _ => path, zip: true);
        }

        // The song a package holds: its SONG_<ID>.bin (a preview-only package has none).
        private static string? songOf(HashSet<string> names) =>
            names.FirstOrDefault(static name => name.StartsWith("SONG_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) is { } song
            && song["SONG_".Length..^".bin".Length].ToLowerInvariant() is var id && SongId().IsMatch(id) ? id : null;

        /// <summary>Identifies the source file for the cache: its container's length and write time.</summary>
        public string Stamp(string name)
        {
            var file = new FileInfo(_locate(name));
            return $"{file.Length:x}{file.LastWriteTimeUtc.Ticks:x}";
        }

        public async Task<byte[]> ReadAsync(string name, CancellationToken cancellationToken)
        {
            if (!Has(name))
                throw new FileNotFoundException($"{name} is not in the song's files.");
            var path = _locate(name);
            if (!_zip)
            {
                if (new FileInfo(path).Length > MaximumFileBytes)
                    throw new InvalidDataException($"{name} exceeds 64 MiB.");
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.First(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (entry.Length > MaximumFileBytes)
                throw new InvalidDataException($"{name} exceeds 64 MiB.");
            using var input = entry.Open();
            var bytes = new byte[entry.Length];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
    }

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex SongId();

    // <UniqueId>_trail_<version>[.zip]; *_trail_stream_* packages hold previews only.
    [GeneratedRegex(@"^\d+_trail_(?<version>\d+)(\.zip)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Package();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();
}
