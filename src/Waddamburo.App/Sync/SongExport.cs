using System.Collections.Concurrent;
using System.Security.Cryptography;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Sync;

/// <summary>A blob the sender can send: its size and how to read it again (charts are serialised anew, not kept).</summary>
public sealed record OutgoingBlob(long Size, Func<Task<Stream>> Open);

/// <summary>
/// Send, the sender's half: the songs a search matches, as rows (<see cref="StoredSong"/>) with their blobs hashed:
/// each chart in its canonical form and drawing data, as the game plays it, and the music as the game reads it.
/// </summary>
internal static class SongExport
{
    /// <summary>
    /// Rows and blobs for the songs of <paramref name="snapshot"/> that <paramref name="query"/> matches (Song Select's
    /// search; empty: every song). A song that can't be read is told to <paramref name="warn"/> and left out.
    /// </summary>
    public static async Task<(List<StoredSong> Rows, Dictionary<string, OutgoingBlob> Blobs)> BuildAsync(SongCatalogSnapshot snapshot,
        CatalogAssetRouter router, string query, Action<string>? warn = null, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var categories = new Dictionary<SongKey, SongCategoryDescriptor>();
        foreach (var category in snapshot.Categories)
            foreach (var key in category.Songs)
                categories.TryAdd(key, category);
        IEnumerable<string> names(SongDescriptor song) =>
            categories.TryGetValue(song.Key, out var category) ? [category.Name] : [];
        var songs = snapshot.Songs.Values
            .Where(song => categories.ContainsKey(song.Key) && (string.IsNullOrWhiteSpace(query) || query.Trim() == "*" || SongBrowse.Matches(song, query, categoriesOf: names)))
            .ToList();
        var blobs = new ConcurrentDictionary<string, OutgoingBlob>(StringComparer.Ordinal);
        var rows = new ConcurrentBag<StoredSong>();
        var done = 0;
        await Parallel.ForEachAsync(songs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (song, token) =>
        {
            try
            {
                if (await rowAsync(song, categories[song.Key], router, blobs, token).ConfigureAwait(false) is { } row)
                    rows.Add(row);
                else
                    warn?.Invoke($"{song.Title.Primary}: no music or no playable course, not sent.");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException
                or ArgumentException or OverflowException or UnauthorizedAccessException)
            {
                warn?.Invoke($"{song.Title.Primary}: {exception.Message} Not sent.");
            }
            progress?.Invoke(Interlocked.Increment(ref done), songs.Count);
        }).ConfigureAwait(false);
        return ([.. rows.OrderBy(static row => row.Source).ThenBy(static row => row.SongId, StringComparer.Ordinal)], new(blobs));
    }

    private static async Task<StoredSong?> rowAsync(SongDescriptor song, SongCategoryDescriptor category, CatalogAssetRouter router,
        ConcurrentDictionary<string, OutgoingBlob> blobs, CancellationToken cancellationToken)
    {
        if (song.AudioAsset is not { } audio)
            return null;
        var charts = new List<StoredChart>();
        foreach (var chart in song.Charts)
        {
            if (chart.Course is not { } course)
                continue;
            var loaded = await router.LoadChartAsync(chart.Key, chart.ChartAsset, cancellationToken).ConfigureAwait(false);
            var (canonical, drawing) = chartBlobs(chart.Key, chart.ChartAsset, course, loaded, router, blobs);
            List<string> duets = [], duetDrawings = [];
            foreach (var duet in chart.DuetChartAssets)
            {
                var (duetChart, duetDrawing) = chartBlobs(chart.Key, duet, course,
                    await router.LoadChartAsync(chart.Key, duet, cancellationToken).ConfigureAwait(false), router, blobs);
                duets.Add(duetChart);
                duetDrawings.Add(duetDrawing);
            }
            charts.Add(new StoredChart(chart.Key.StableId, course, chart.Level, chart.DifficultyName, canonical, drawing,
                (long)loaded.Duration.TotalMilliseconds, [.. duets], [.. duetDrawings], loaded.ShinuchiBase, chart.HasBranches, chart.OnlineId,
                router.UploadBlocked(chart.ChartAsset)));
        }
        if (charts.Count == 0)
            return null;
        return new StoredSong(song.Key.Source, song.Key.StableId, song.Title.Primary,
            new StoredCategory(category.Key.StableId, category.Name, category.SortOrder, category.Genre, category.Colour,
                category.OutlineColour, category.Description.IsDefaultOrEmpty ? null : [.. category.Description]),
            await fileBlob(audio, router, blobs, cancellationToken).ConfigureAwait(false), [.. charts],
            song.Title.Japanese, song.Title.English, song.Artist, song.Subtitle, song.EnglishSubtitle, song.Creator,
            song.PreviewStart is { } preview ? (long)preview.TotalMilliseconds : null, song.Bpm,
            song.Length is { } length ? (long)length.TotalMilliseconds : null,
            song.WaiwaiComposition is { } composition ? await fileBlob(composition, router, blobs, cancellationToken).ConfigureAwait(false) : null,
            song.ScenePresets.IsDefaultOrEmpty ? null : [.. song.ScenePresets], song.DemoPlay, song.OnlineSetId);
    }

    // A chart's canonical bytes and drawing data, hashed; sent later by serialising the chart again.
    private static (string Chart, string Drawing) chartBlobs(ChartKey key, CatalogAssetKey asset, TaikoCourse course, PlayableChart chart,
        CatalogAssetRouter router, ConcurrentDictionary<string, OutgoingBlob> blobs)
    {
        var canonical = ChartHash.Serialize(chart, course);
        var drawing = ChartDrawing.Serialize(chart);
        async Task<PlayableChart> load() => await router.LoadChartAsync(key, asset).ConfigureAwait(false);
        var chartHash = SongStore.Hash(canonical);
        var drawingHash = SongStore.Hash(drawing);
        blobs.TryAdd(chartHash, new OutgoingBlob(canonical.Length, async () => new MemoryStream(ChartHash.Serialize(await load().ConfigureAwait(false), course))));
        blobs.TryAdd(drawingHash, new OutgoingBlob(drawing.Length, async () => new MemoryStream(ChartDrawing.Serialize(await load().ConfigureAwait(false)))));
        return (chartHash, drawingHash);
    }

    // A file as the game reads it (music, composition), hashed while read.
    private static async Task<string> fileBlob(CatalogAssetKey asset, CatalogAssetRouter router, ConcurrentDictionary<string, OutgoingBlob> blobs,
        CancellationToken cancellationToken)
    {
        await using var stream = await router.OpenReadAsync(asset, cancellationToken).ConfigureAwait(false);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            size += read;
        }
        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        blobs.TryAdd(hash, new OutgoingBlob(size, async () => await router.OpenReadAsync(asset).ConfigureAwait(false)));
        return hash;
    }
}
