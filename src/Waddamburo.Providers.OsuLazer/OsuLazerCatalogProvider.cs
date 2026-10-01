using Waddamburo.Catalog;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Publishes the native osu!taiko beatmaps of an osu!lazer install. A set's charts are grouped per
/// audio file (a set may hold unrelated songs) and dealt into Taiko courses by <see cref="OsuCourseLayout"/>;
/// the player's lazer collections become categories after "All Songs".
/// </summary>
public sealed class OsuLazerCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    private readonly string _root;

    /// <summary>Reads lazer's data folder (with client.realm and files/), see <see cref="FindDataFolder"/>.</summary>
    public OsuLazerCatalogProvider(string dataFolder, CatalogProviderId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _root = Path.GetFullPath(dataFolder);
        Id = id ?? new CatalogProviderId("osu-lazer");
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.OsuLazer;

    /// <summary>
    /// The lazer data folder <paramref name="configured"/> points at (the folder, its client.realm, or
    /// a folder whose storage.ini moved the data elsewhere), or the default install's when null; null
    /// when there is none.
    /// </summary>
    public static string? FindDataFolder(string? configured)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string[] candidates = configured is not null ? [configured] :
        [
            Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "osu"),
            Path.Combine(home, ".var", "app", "sh.ppy.osu", "data", "osu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"),
        ];
        foreach (var candidate in candidates)
        {
            var root = Path.GetFileName(candidate) == "client.realm" ? Path.GetDirectoryName(candidate)! : candidate;
            // storage.ini's FullPath moves the data; follow a few hops.
            for (var hop = 0; hop < 4; hop++)
            {
                var ini = Path.Combine(root, "storage.ini");
                var target = File.Exists(ini) ? File.ReadLines(ini)
                    .Select(static line => line.TrimStart('﻿').Split('=', 2))
                    .Where(static pair => pair.Length == 2 && pair[0].Trim() == "FullPath")
                    .Select(static pair => pair[1].Trim()).LastOrDefault() : null;
                if (string.IsNullOrEmpty(target) || target == root)
                    break;
                root = target;
            }
            if (File.Exists(Path.Combine(root, "client.realm")) && Directory.Exists(Path.Combine(root, "files")))
                return root;
        }
        return null;
    }

    public ValueTask<SongCatalogContribution> ScanAsync(
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CatalogScanProgress(Id, "realm", 0, null));
        var snapshot = OsuLazerRealmReader.Read(Path.Combine(_root, "client.realm"));
        cancellationToken.ThrowIfCancellationRequested();
        var contribution = CreateContribution(snapshot, Id);
        progress?.Report(new CatalogScanProgress(Id, "complete", contribution.Songs.Length, contribution.Songs.Length));
        return ValueTask.FromResult(contribution);
    }

    public static SongCatalogContribution CreateContribution(
        OsuLazerSnapshot snapshot,
        CatalogProviderId? id = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var provider = id ?? new CatalogProviderId("osu-lazer");
        var songs = new List<SongDescriptor>();
        var songByMd5 = new Dictionary<string, SongKey>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<CatalogDiagnostic>();
        foreach (var set in snapshot.BeatmapSets.Where(static set => !set.DeletePending))
        {
            var files = set.Files.GroupBy(static file => file.Filename.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First().Hash, StringComparer.OrdinalIgnoreCase);
            var byAudio = set.Beatmaps
                .Where(static beatmap => !beatmap.Hidden && beatmap.Ruleset == "taiko" && isHash(beatmap.FileHash))
                .GroupBy(static beatmap => beatmap.AudioFilename.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            foreach (var group in byAudio)
            {
                if (!files.TryGetValue(group.Key, out var audioHash) || !isHash(audioHash))
                {
                    diagnostics.Add(new CatalogDiagnostic(CatalogDiagnosticSeverity.Warning, "OSU_AUDIO_NOT_FOUND",
                        $"Beatmap set {set.Id} does not resolve its audio file '{group.Key}'.", provider));
                    continue;
                }
                var entries = OsuCourseLayout.Deal(group, static beatmap => beatmap.DifficultyName, static beatmap => beatmap.StarRating);
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    var first = entry.Values.First();
                    // ponytail: keyed by set, audio and entry number; adding a chart in lazer can reshuffle
                    // entries (and their scores). Key by the lowest chart's hash if that matters.
                    var songKey = new SongKey(SongSourceKind.OsuLazer, $"{set.Id:N}:{audioHash[..8]}:{index}");
                    var charts = entry.Select(pair => new SongChartDescriptor(
                        new ChartKey(songKey, pair.Value.FileHash),
                        pair.Value.DifficultyName.Length == 0 ? "Taiko" : pair.Value.DifficultyName,
                        new CatalogAssetKey(provider, $"chart:{pair.Value.FileHash}:{OsuCourseLayout.Level(pair.Value.StarRating)}"),
                        pair.Key,
                        OsuCourseLayout.Level(pair.Value.StarRating))).ToArray();
                    var artist = orEnglish(first.ArtistUnicode, first.Artist);
                    // A set dealt into several entries names each entry's charts so they can be told apart.
                    var subtitle = entries.Count == 1 ? artist : $"{artist} [{string.Join(" / ", entry.Values.Select(static b => b.DifficultyName))}]";
                    songs.Add(new SongDescriptor(
                        songKey,
                        new SongTitle(orEnglish(first.TitleUnicode, first.Title, "?"),
                            japanese: nullIfBlank(first.TitleUnicode), english: nullIfBlank(first.Title)),
                        artist,
                        charts,
                        new CatalogAssetKey(provider, $"file:{audioHash}"),
                        first.PreviewTime > 0 ? TimeSpan.FromMilliseconds(first.PreviewTime) : null,
                        subtitle));
                    foreach (var beatmap in entry.Values.Where(static beatmap => beatmap.Md5Hash.Length != 0))
                        songByMd5.TryAdd(beatmap.Md5Hash, songKey);
                }
            }
        }

        var orderedSongs = songs
            .OrderBy(static song => song.Title.English ?? song.Title.Primary, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static song => song.Key.StableId, StringComparer.Ordinal)
            .ToArray();
        var categories = new List<SongCategoryDescriptor>
        {
            new(new CategoryKey(SongSourceKind.OsuLazer, "all"), "All Songs", 0, orderedSongs.Select(static song => song.Key)),
        };
        var order = orderedSongs.Select(static (song, index) => (song.Key, index)).ToDictionary(static pair => pair.Key, static pair => pair.index);
        foreach (var collection in snapshot.Collections.OrderBy(static collection => collection.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = collection.BeatmapMd5Hashes
                .Select(md5 => songByMd5.TryGetValue(md5, out var key) ? key : null)
                .OfType<SongKey>().Distinct().OrderBy(key => order[key]).ToArray();
            if (members.Length != 0 && !string.IsNullOrWhiteSpace(collection.Name))
                categories.Add(new SongCategoryDescriptor(new CategoryKey(SongSourceKind.OsuLazer, $"collection:{collection.Id:N}"),
                    collection.Name, categories.Count, members));
        }
        return new SongCatalogContribution(provider, SongSourceKind.OsuLazer, orderedSongs, categories, diagnostics);
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id)
            throw new ArgumentException("The asset belongs to a different catalog provider.", nameof(asset));
        cancellationToken.ThrowIfCancellationRequested();
        var parts = asset.StableId.Split(':');
        return ValueTask.FromResult<Stream>(File.OpenRead(storePath(parts.Length > 1 ? parts[1] : "")));
    }

    public async ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id)
            throw new ArgumentException("The chart asset belongs to a different catalog provider.", nameof(asset));
        var parts = asset.StableId.Split(':');
        if (parts is not ["chart", var hash, var levelText] || !int.TryParse(levelText, out var level))
            throw new ArgumentException("Not an osu!lazer chart asset.", nameof(asset));
        var text = await File.ReadAllTextAsync(storePath(hash), cancellationToken).ConfigureAwait(false);
        return OsuTaikoChartReader.Read(text, chart, level);
    }

    // lazer's file store: files/<h>/<hh>/<sha-256>.
    private string storePath(string hash) => isHash(hash)
        ? Path.Combine(_root, "files", hash[..1], hash[..2], hash)
        : throw new ArgumentException("Not an osu!lazer file hash.", nameof(hash));

    private static bool isHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);

    private static string? nullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string orEnglish(string unicode, string english, string fallback = "") =>
        nullIfBlank(unicode) ?? nullIfBlank(english) ?? fallback;
}
