using Waddamburo.Catalog;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>Publishes native osu!taiko beatmap sets through the shared catalog contract.</summary>
public sealed class OsuLazerCatalogProvider : ISongCatalogProvider
{
    private readonly string _databasePath;

    public OsuLazerCatalogProvider(string databasePath, CatalogProviderId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        Id = id ?? new CatalogProviderId("osu-lazer");
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.OsuLazer;

    public ValueTask<SongCatalogContribution> ScanAsync(
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CatalogScanProgress(Id, "realm", 0, null));
        var snapshot = OsuLazerRealmReader.Read(_databasePath);
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
        var diagnostics = new List<CatalogDiagnostic>();
        foreach (var set in snapshot.BeatmapSets.Where(static set => !set.DeletePending))
        {
            var beatmaps = set.Beatmaps
                .Where(static beatmap => string.Equals(beatmap.Ruleset, "taiko", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static beatmap => beatmap.DifficultyName, StringComparer.Ordinal)
                .ThenBy(static beatmap => beatmap.FileHash, StringComparer.Ordinal)
                .ToArray();
            if (beatmaps.Length == 0)
                continue;

            var songKey = new SongKey(
                SongSourceKind.OsuLazer,
                string.IsNullOrWhiteSpace(set.Hash) ? set.Id.ToString("N") : set.Hash);
            var first = beatmaps[0];
            var charts = beatmaps.Select(beatmap =>
            {
                var stableId = string.IsNullOrWhiteSpace(beatmap.FileHash) ? beatmap.Id.ToString("N") : beatmap.FileHash;
                return new SongChartDescriptor(
                    new ChartKey(songKey, stableId),
                    beatmap.DifficultyName,
                    new CatalogAssetKey(provider, $"beatmap:{stableId}"));
            }).ToArray();
            var audioHash = set.Files.FirstOrDefault(file =>
                string.Equals(file.Filename, first.AudioFilename, StringComparison.Ordinal))?.Hash;
            CatalogAssetKey? audio = null;
            if (!string.IsNullOrWhiteSpace(audioHash))
                audio = new CatalogAssetKey(provider, $"file:{audioHash}");
            else
                diagnostics.Add(new CatalogDiagnostic(
                    CatalogDiagnosticSeverity.Warning,
                    "OSU_AUDIO_NOT_FOUND",
                    $"Beatmap set '{songKey.StableId}' does not resolve its audio file.",
                    provider));

            songs.Add(new SongDescriptor(
                songKey,
                new SongTitle(
                    string.IsNullOrWhiteSpace(first.TitleUnicode) ? first.Title : first.TitleUnicode,
                    japanese: string.IsNullOrWhiteSpace(first.TitleUnicode) ? null : first.TitleUnicode,
                    english: first.Title),
                string.IsNullOrWhiteSpace(first.ArtistUnicode) ? first.Artist : first.ArtistUnicode,
                charts,
                audio));
        }

        var orderedSongs = songs
            .OrderBy(static song => song.Title.Primary, StringComparer.Ordinal)
            .ThenBy(static song => song.Key.StableId, StringComparer.Ordinal)
            .ToArray();
        var category = new SongCategoryDescriptor(
            new CategoryKey(SongSourceKind.OsuLazer, "all"),
            "osu!lazer",
            0,
            orderedSongs.Select(static song => song.Key));
        return new SongCatalogContribution(
            provider,
            SongSourceKind.OsuLazer,
            orderedSongs,
            [category],
            diagnostics);
    }
}
