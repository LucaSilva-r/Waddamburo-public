using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// A player's own modchart for one chart and course: &lt;folder&gt;/&lt;chart hash&gt;.wddmod (<see cref="ChartModFile"/>), over
/// the chart's own effects (a TJA's Song.wddmod, applied when the chart loads). Effects only change what is drawn, so the
/// chart's hash and scores stay as they are: any song, stock and TJA alike, can have one.
/// </summary>
internal static class PlayerModcharts
{
    /// <summary>The file for <paramref name="chart"/> on <paramref name="course"/>.</summary>
    public static string PathFor(string folder, PlayableChart chart, TaikoCourse course) =>
        Path.Combine(folder, ChartHash.Compute(chart, course) + ChartModFile.Extension);

    /// <summary>
    /// <paramref name="chart"/> with the player's modchart when its file exists; the path is printed either way, so one can
    /// be written for the chart. A broken file is reported and the chart keeps its own effects.
    /// </summary>
    public static PlayableChart Apply(string folder, PlayableChart chart, TaikoCourse course)
    {
        var path = PathFor(folder, chart, course);
        if (!File.Exists(path))
        {
            Console.WriteLine($"Modchart: none for this chart ({path}).");
            return chart;
        }
        return read(path, course) is { } tracks ? ChartModFile.Apply(chart, tracks) : chart;
    }

    /// <summary>The player's gameplay layout: one file for every chart, beside the per-chart modcharts.</summary>
    public static string LayoutPath(string folder) => Path.Combine(folder, "layout" + ChartModFile.Extension);

    /// <summary><paramref name="chart"/> with the player's layout, unless it has modchart changes of its own.</summary>
    public static PlayableChart ApplyLayout(string folder, PlayableChart chart) =>
        File.Exists(LayoutPath(folder)) && read(LayoutPath(folder), null) is { } layout ? ChartModFile.ApplyLayout(chart, layout) : chart;

    // A broken file is reported and not used.
    private static System.Collections.Immutable.ImmutableDictionary<string, System.Collections.Immutable.ImmutableArray<ChartModKeyframe>>? read(
        string path, TaikoCourse? course)
    {
        try
        {
            if (new FileInfo(path).Length > ChartModFile.MaxBytes)
                throw new InvalidDataException("The modchart file is too large.");
            var tracks = ChartModFile.ReadTracks(File.ReadAllBytes(path), course);
            Console.WriteLine($"Modchart: {tracks.Count} track(s) from {path}.");
            return tracks;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Modchart {path} not used: {exception.Message}");
            return null;
        }
    }
}
