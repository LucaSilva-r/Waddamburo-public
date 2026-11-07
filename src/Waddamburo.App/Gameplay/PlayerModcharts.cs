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
        try
        {
            if (new FileInfo(path).Length > ChartModFile.MaxBytes)
                throw new InvalidDataException("The modchart file is too large.");
            var tracks = ChartModFile.ReadTracks(File.ReadAllBytes(path), course);
            Console.WriteLine($"Modchart: {tracks.Count} track(s) from {path}.");
            return ChartModFile.Apply(chart, tracks);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Modchart {path} not used: {exception.Message}");
            return chart;
        }
    }
}
