using Waddamburo.Catalog;
using Waddamburo.Game.Dan;

namespace Waddamburo.Features.Dan;

/// <summary>The song library dan courses are matched against: its songs, their charts' hashes, reading a chart.</summary>
public sealed record DanLibrary(SongCatalogSnapshot Snapshot, ChartHashes Hashes,
    Func<ChartKey, CatalogAssetKey, CancellationToken, ValueTask<PlayableChart>> LoadChart);

/// <summary>
/// A source of dan series, like the song catalog's providers: TaikOnline's official courses, the player's own TJA
/// courses. <see cref="DanModule"/> lists every provider's series, in the order given.
/// </summary>
public interface IDanCourseProvider
{
    /// <summary>The series known at once (saved from a past start); empty: none.</summary>
    IReadOnlyList<DanSeries> Saved { get; }

    /// <summary>The series as they are now, read in the background; failures are logged and give what is known.</summary>
    Task<IReadOnlyList<DanSeries>> LoadAsync(DanLibrary library);
}
