using System.Text.Json.Serialization;
using Waddamburo.Catalog;

namespace Waddamburo.App.Sync;

/// <summary>The files a receiver must have before it can publish the chart and audio files of a song.</summary>
internal sealed record SongSyncManifest(string[] Shared, string[][] Songs)
{
    public static async Task<SongSyncManifest> CreateAsync(SyncLibrary library, HashSet<string> paths, CancellationToken token)
    {
        var provider = library.OpenCatalog!();
        var catalog = await provider.ScanAsync(null, token).ConfigureAwait(false);
        var resolver = (ICatalogAssetResolver)provider;
        var songs = new List<string[]>(catalog.Songs.Length);
        foreach (var song in catalog.Songs)
        {
            token.ThrowIfCancellationRequested();
            var files = new HashSet<string>(StringComparer.Ordinal);
            var assets = song.Charts.SelectMany(static chart => chart.DuetChartAssets.Prepend(chart.ChartAsset))
                .Concat(new[] { song.AudioAsset, song.WaiwaiComposition }.OfType<CatalogAssetKey>()).Distinct();
            foreach (var asset in assets)
            {
                Stream stream;
                try { stream = await resolver.OpenReadAsync(asset, token).ConfigureAwait(false); }
                catch (FileNotFoundException) { continue; } // a source can itself be missing an asset
                catch (DirectoryNotFoundException) { continue; }
                await using (stream)
                {
                    if (stream is not FileStream file || library.RelativePath(file.Name) is not { } raw)
                        throw new IOException("A song file is outside the library inventory. Retry after the source library is stable.");
                    var path = library.Nijiiro is null ? raw : NijiiroSync.ReceiverPath(raw);
                    if (!paths.Contains(path))
                        throw new IOException("A song file is outside the library inventory. Retry after the source library is stable.");
                    files.Add(path);
                    if (library.Nijiiro is not null && paths.Contains(path + ".preview"))
                        files.Add(path + ".preview");
                }
            }
            if (files.Count > 0)
                songs.Add([.. files.Order(StringComparer.Ordinal)]);
        }
        var songFiles = library.Kind == "green" ? songs.SelectMany(static song => song).ToHashSet(StringComparer.Ordinal) : null;
        var shared = paths.Where(path => isShared(library.Kind, path)
            || songFiles is not null && !songFiles.Contains(path) && Cli.GreenData.IsEssentialFile(path))
            .Order(StringComparer.Ordinal).ToArray();
        return new SongSyncManifest(shared, [.. songs]);
    }

    // Catalog inputs shared by multiple songs. The receiver must install these before publishing a new song.
    private static bool isShared(string kind, string path) => kind switch
    {
        "green" or "stock" => path is "data/musicinfo.xml" or "data/fumen/tuning.bin"
            || path.StartsWith("data/config/", StringComparison.Ordinal) && path.EndsWith("/musicinfo.xml", StringComparison.Ordinal),
        "wii" => path is "files/sheet/musicinfo/bin/musicinfo.bin" or "files/sheet/tuning/bin/tuning.bin",
        "wiiu" => path.EndsWith("/Common/database/db_pack.drp", StringComparison.Ordinal)
            || path.EndsWith("/code/app.xml", StringComparison.Ordinal)
            || path.StartsWith("dlc/", StringComparison.Ordinal) && path.EndsWith("/musicInfo.drp", StringComparison.Ordinal),
        "nijiiro" => path.StartsWith(NijiiroSync.Part + "/datatable/", StringComparison.Ordinal),
        "osu-lazer" => path == "osu/client.realm",
        "osu-stable" => path is "osu/osu!.db" or "osu/collection.db",
        _ => false,
    };
}

[JsonSerializable(typeof(SongSyncManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SongSyncJson : JsonSerializerContext;
