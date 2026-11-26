using Waddamburo.Game.Dan;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Dan;

/// <summary>
/// The official courses: TaikOnline's dan catalogue (no account needed), saved beside the scores so the dojo opens
/// offline once it has been online. Without either, it has no series.
/// </summary>
/// <param name="path">Where the catalogue is saved; null: kept in memory only.</param>
/// <param name="server">TaikOnline; null: offline.</param>
public sealed class OnlineDanCourses(string? path, Uri? server, bool insecure) : IDanCourseProvider
{
    private DanCatalogue? _catalogue = path is null ? null : DanCatalogue.Load(path);

    public IReadOnlyList<DanSeries> Saved => _catalogue?.Series ?? [];

    public async Task<IReadOnlyList<DanSeries>> LoadAsync(DanLibrary library)
    {
        if (server is null)
            return Saved;
        try
        {
            using var http = ScoreClient.CreateHttp(server, insecure);
            var fresh = await new ScoreClient(http).DanCatalogueAsync().ConfigureAwait(false);
            _catalogue = fresh;
            if (path is not null)
                fresh.Save(path);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Console.WriteLine($"Dan courses not refreshed: {exception.Message}");
        }
        return Saved;
    }
}
