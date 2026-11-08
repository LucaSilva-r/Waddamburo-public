using System.IO.Compression;
using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;
using Waddamburo.Providers.Stock;

namespace Waddamburo.App.Cli;

/// <summary>
/// --export-charts=FILE: every chart of the game's own data, of every other release in stock_folders and of every
/// Nijiiro installation in nijiiro_folder (home mode or not), each install read on its own so every version of a chart
/// is there (once per song and hash), as gzipped JSON lines, one chart import each (<see cref="ChartUpload"/> plus its sha256), for TaikOnline's
/// <c>php artisan app:import-waddamburo-charts</c>. A Nijiiro install may hold fan-made and other games' songs, so
/// only exports of unmodified dumps count as official there (its --nijiiro-dumps); the rest still get their notes and songs.
/// </summary>
internal static class ChartExport
{
    public static int Run(string dataRoot, ArcadeSettings arcade, string path)
    {
        ISongCatalogProvider[] providers =
        [
            .. new[] { dataRoot }.Concat(ArcadeSettings.Folders(arcade.StockFolders).Select(StockCatalogProvider.FindDataRoot).OfType<string>())
                .Where(StockCatalogProvider.IsStockData).Distinct().Select(static root => new StockCatalogProvider(root)),
            .. ArcadeSettings.Folders(arcade.NijiiroFolder).Select(folder => new NijiiroCatalogProvider(folder,
                keys: NijiiroKeys.Parse(arcade.NijiiroDatatableKey, arcade.NijiiroFumenKey)))
                .Where(static installed => installed.Problem is null),
        ];
        var exported = 0;
        var skipped = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var file = File.Create(path);
        using var output = new GZipStream(file, CompressionLevel.Optimal);
        foreach (var provider in providers)
        foreach (var song in provider.ScanAsync(null, CancellationToken.None).AsTask().GetAwaiter().GetResult().Songs)
        {
            foreach (var chart in song.Charts)
            {
                if (chart.Course is not { } course)
                    continue;
                PlayableChart playable;
                try
                {
                    playable = ((IPlayableChartProvider)provider).LoadChartAsync(chart.Key, chart.ChartAsset).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException
                    or IOException or OverflowException or ArgumentException)
                {
                    skipped++;
                    continue;
                }
                var sha = ChartHash.Compute(playable, course);
                // Once per song: a chart a Nijiiro song shares with a stock one still joins (and proves) its Nijiiro song.
                if (!seen.Add($"{song.Key}|{sha}"))
                    continue;
                var upload = ChartUpload.From(playable, course, song);
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    writer.WriteString("sha256", sha);
                    // A 譜面分岐 chart's hash before branching was played (clients up to v0.18 read its normal
                    // route alone): TaikOnline marks that chart legacy and keeps its plays apart.
                    if (playable.Branching is { SameNotes: false })
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
        Console.WriteLine($"Exported {exported} charts from {providers.Length} installs to {path} ({skipped} unreadable, skipped).");
        return 0;
    }
}
