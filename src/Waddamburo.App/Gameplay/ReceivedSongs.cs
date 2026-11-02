using System.Collections.Concurrent;
using Waddamburo.Catalog;
using Waddamburo.Game.Online;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// Songs another player sent this session, to play together: kept in memory only, so they're gone when the game closes.
/// They keep the host's song key, so both games name the same song; Song Select lists one only while it's being played.
/// </summary>
/// <remarks>ponytail: every song received stays until the game closes (64 MB at most each); drop old ones if that grows.</remarks>
internal sealed class ReceivedSongs : ICatalogAssetResolver, IPlayableChartProvider
{
    private readonly ConcurrentDictionary<string, PlayableChart> _charts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _audio = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<SongKey, SongDescriptor> _songs = new();

    public CatalogProviderId Id { get; } = new("online-received");

    /// <summary>A song received earlier this session; null when none was.</summary>
    public SongDescriptor? Find(SongKey song) => _songs.GetValueOrDefault(song);

    /// <summary>Keeps a song the other player sent, and returns how the catalog describes it.</summary>
    public SongDescriptor Add(SongPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var song = package.Song;
        var audio = new CatalogAssetKey(Id, $"audio:{song.Source}:{song.StableId}");
        _audio[audio.StableId] = package.Audio;
        var charts = package.Charts.Select(entry =>
        {
            var asset = new CatalogAssetKey(Id, $"chart:{song.Source}:{song.StableId}:{entry.StableId}");
            _charts[asset.StableId] = entry.Chart;
            return new SongChartDescriptor(entry.Chart.Key, entry.DifficultyName, asset, entry.Course, entry.Level);
        }).ToArray();
        var descriptor = new SongDescriptor(song, package.Title, package.Artist, charts, audio, package.PreviewStart, package.Subtitle);
        _songs[song] = descriptor;
        return descriptor;
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _audio.TryGetValue(asset.StableId, out var bytes)
            ? ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new ArgumentException($"No received song has '{asset.StableId}'.", nameof(asset));

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _charts.TryGetValue(asset.StableId, out var loaded)
            ? ValueTask.FromResult(loaded)
            : throw new ArgumentException($"No received song has '{asset.StableId}'.", nameof(asset));
}
