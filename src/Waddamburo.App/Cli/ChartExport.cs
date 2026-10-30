using System.IO.Compression;
using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;
using Waddamburo.Providers.Stock;

namespace Waddamburo.App.Cli;

/// <summary>
/// --export-charts=FILE: every stock chart, and the Nijiiro charts of the songs --nijiiro-songs=FILE lists (the
/// Nijiiro folder from the settings, home mode or not; TaikOnline's database/data/nijiiro-song-ids.txt, one id a
/// line, # comments): a Nijiiro install may hold community songs that must not rank. As gzipped JSON lines, one chart import each (<see cref="ChartUpload"/> plus its sha256), for
/// TaikOnline's <c>php artisan app:import-waddamburo-charts</c>.
/// </summary>
internal static class ChartExport
{
    public static int Run(string dataRoot, ArcadeSettings arcade, string path, string? nijiiroSongs)
    {
        var official = nijiiroSongs is null ? null : File.ReadLines(nijiiroSongs)
            .Select(static line => line.Split('#', 2)[0].Trim()).Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        ISongCatalogProvider[] providers =
        [
            .. StockCatalogProvider.IsStockData(dataRoot) ? [new StockCatalogProvider(dataRoot)] : Array.Empty<ISongCatalogProvider>(),
            .. official is not null && arcade.NijiiroFolder is { } nijiiro && new NijiiroCatalogProvider(nijiiro) is var installed
                && installed.Problem is null ? [installed] : Array.Empty<ISongCatalogProvider>(),
        ];
        using var catalog = new GlobalSongCatalog(providers);
        var snapshot = catalog.RefreshAsync().AsTask().GetAwaiter().GetResult();
        var assets = new CatalogAssetRouter(providers);
        var exported = 0;
        var skipped = 0;
        using var file = File.Create(path);
        using var output = new GZipStream(file, CompressionLevel.Optimal);
        var unlisted = 0;
        foreach (var song in snapshot.Songs.Values)
        {
            if (song.Key.Source == SongSourceKind.Nijiiro && !official!.Contains(song.Key.StableId))
            {
                unlisted++;
                continue;
            }
            foreach (var chart in song.Charts)
            {
                if (chart.Course is not { } course)
                    continue;
                PlayableChart playable;
                try
                {
                    playable = assets.LoadChartAsync(chart.Key, chart.ChartAsset).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException
                    or IOException or OverflowException or ArgumentException)
                {
                    skipped++;
                    continue;
                }
                var upload = ChartUpload.From(playable, course, song);
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    writer.WriteString("sha256", ChartHash.Compute(playable, course));
                    // A 譜面分岐 chart's hash before branching was played (clients up to v0.18 read its normal
                    // route alone): TaikOnline marks that chart legacy and keeps its plays apart.
                    if (playable.Branching is not null)
                        writer.WriteString("legacy_sha256", ChartHash.Compute(playable.OnRoute(ChartBranch.Normal), course));
                    writer.WriteString("notes", upload.Notes);
                    writer.WriteString("title", upload.Title);
                    writer.WriteString("subtitle", upload.Subtitle);
                    writer.WriteString("source", upload.Source);
                    writer.WriteNumber("course", upload.Course);
                    writer.WriteString("song_key", upload.SongKey);
                    writer.WriteString("title_en", upload.TitleEn);
                    writer.WriteString("subtitle_en", upload.SubtitleEn);
                    writer.WriteString("difficulty", upload.Difficulty);
                    if (upload.OsuBeatmapId is { } beatmap)
                        writer.WriteNumber("osu_beatmap_id", beatmap);
                    if (upload.OsuBeatmapsetId is { } beatmapset)
                        writer.WriteNumber("osu_beatmapset_id", beatmapset);
                    if (upload.Level is { } level)
                        writer.WriteNumber("level", level);
                    else
                        writer.WriteNull("level");
                    writer.WriteEndObject();
                }
                output.WriteByte((byte)'\n');
                exported++;
            }
        }
        Console.WriteLine($"Exported {exported} charts to {path} ({skipped} unreadable, skipped; "
            + (official is null ? "Nijiiro left out without --nijiiro-songs)." : $"{unlisted} Nijiiro songs not listed, left out)."));
        return 0;
    }
}
