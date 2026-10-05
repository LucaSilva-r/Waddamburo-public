using System.Collections.Concurrent;
using System.Diagnostics;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Online;

/// <summary>
/// Chart key -> canonical hash, remembered in the score store (when there is one) so server bests
/// can be matched to the library's charts. Hashing parses the chart, so the whole library is done once
/// in the background and new charts as they are met.
/// </summary>
internal sealed class ChartHashes(ScoreStore? store,
    Func<ChartKey, CatalogAssetKey, CancellationToken, ValueTask<PlayableChart>> loadChart)
{
    private readonly ConcurrentDictionary<ChartKey, string> _known = new();

    /// <summary>Where the library hashing shows its progress (the notice sidebar).</summary>
    public JobBoard? Jobs { get; init; }

    /// <summary>The chart's hash (null: no course, so never played on its own).</summary>
    public async ValueTask<string?> HashAsync(SongChartDescriptor chart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (chart.Course is not { } course)
            return null;
        if (_known.TryGetValue(chart.Key, out var hash))
            return hash;
        if (store?.ChartHashOf(chart.Key.ToString()) is { } stored)
            return _known[chart.Key] = stored;
        hash = ChartHash.Compute(await loadChart(chart.Key, chart.ChartAsset, cancellationToken).ConfigureAwait(false), course);
        store?.SaveChartHashes([(chart.Key.ToString(), hash)]);
        return _known[chart.Key] = hash;
    }

    /// <summary>Hashes every library chart the store does not know yet (unreadable charts are skipped).</summary>
    public void HashLibraryInBackground(IEnumerable<SongDescriptor> songs)
    {
        if (store is null)
            return;
        var charts = songs.SelectMany(static song => song.Charts).Where(static chart => chart.Course is not null).ToList();
        _ = Task.Run(async () =>
        {
            var known = store.HashedCharts();
            var missing = charts.Where(chart => !known.Contains(chart.Key.ToString())).ToList();
            if (missing.Count == 0)
                return;
            var clock = Stopwatch.StartNew();
            var job = Jobs?.Start(Strings.T("job.hash_library"), missing.Count);
            var batch = new List<(string, string)>();
            var done = 0;
            foreach (var chart in missing)
            {
                job?.Report(done++);
                try
                {
                    var hash = ChartHash.Compute(await loadChart(chart.Key, chart.ChartAsset, CancellationToken.None)
                        .ConfigureAwait(false), chart.Course!.Value);
                    _known[chart.Key] = hash;
                    batch.Add((chart.Key.ToString(), hash));
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException
                    or IOException or OverflowException or ArgumentException)
                {
                }
                if (batch.Count >= 200)
                {
                    store.SaveChartHashes(batch);
                    batch.Clear();
                }
            }
            store.SaveChartHashes(batch);
            job?.Complete();
            Console.WriteLine($"Hashed {missing.Count} library charts in {clock.Elapsed.TotalSeconds:0.0} s.");
        });
    }
}
