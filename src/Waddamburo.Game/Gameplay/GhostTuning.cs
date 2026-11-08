using System.Globalization;
using System.Xml.Linq;

namespace Waddamburo.Game.Gameplay;

/// <summary>One of AI battle's spread patterns: the spread grows or shrinks by <see cref="VariancePercent"/>.</summary>
public readonly record struct GhostPattern(int TraceType, int TraceProbability, int VariancePercent);

/// <summary>
/// An AI-don level from the game's <c>config/*/ghost/ghosttuning.xml</c> (<c>tuning_lv0..11</c>): its hit spread
/// (variances in ms², before the player's own timing is known, and the floor), three spread patterns with the leads
/// they cover, how many of the player's hits it learns from, and its territory points.
/// </summary>
public sealed record GhostLevel(
    int VarianceFirst,
    int VarianceSecond,
    IReadOnlyList<GhostPattern> Patterns,
    IReadOnlyList<(int Inferior, int Superior)> PatternNodes,
    int SampleSize,
    int ScoreGreat,
    int ScoreGood,
    int ScoreBad);

public static class GhostTuning
{
    /// <summary>The levels in order (lv0 first); empty when the file has none.</summary>
    public static IReadOnlyList<GhostLevel> Parse(string xml)
    {
        var levels = new SortedDictionary<int, GhostLevel>();
        foreach (var element in XDocument.Parse(xml).Root?.Elements() ?? [])
        {
            if (!element.Name.LocalName.StartsWith("tuning_lv", StringComparison.Ordinal)
                || !int.TryParse(element.Name.LocalName.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                continue;
            int value(XElement parent, string name) =>
                int.Parse((string?)parent.Element(name) ?? throw new InvalidDataException($"ghosttuning.xml: {element.Name} has no {name}."),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            levels[index] = new GhostLevel(
                value(element, "defvariance_first"),
                value(element, "defvariance_second"),
                [.. element.Elements("pattern").Select(pattern => new GhostPattern(value(pattern, "tracetype"), value(pattern, "traceprob"), value(pattern, "variancef")))],
                [.. element.Elements("patternnode").Select(node => (value(node, "inferior"), value(node, "superior")))],
                value(element, "statistics_samplesize"),
                value(element, "hitscore_ryo"),
                value(element, "hitscore_ka"),
                value(element, "hitscore_fuka"));
        }
        return [.. levels.Values];
    }

    /// <summary>The installation's file (<c>data/config/&lt;cabinet&gt;/ghost/ghosttuning.xml</c>), or none.</summary>
    public static IReadOnlyList<GhostLevel> Load(string dataRoot)
    {
        var config = Path.Combine(dataRoot, "config");
        var file = Directory.Exists(config)
            ? Directory.EnumerateFiles(config, "ghosttuning.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        return file is null ? [] : Parse(File.ReadAllText(file));
    }
}

/// <summary>
/// AI-don's hit timing: a ghost of the player's own (traced, every battle: AI-don ends each section within a few notes of
/// the player). Each hit replays one of the player's last <see cref="GhostLevel.SampleSize"/> hit offsets, picked at
/// random, its distance from their median scaled by the pattern for the current lead (a normal draw with the level's
/// first variance until there are enough). A normal draw with the player's variance instead made AI-don too easy
/// (testers): a few late hits widen the variance of a mostly tight player, and every AI hit then plays that loose.
/// </summary>
/// <remarks>
/// ponytail: the pattern choice (behind: the second pattern, ahead: the third, else the first) is a reading of
/// ghosttuning.xml, not traced; defvariance_second is unused (as a floor it made AI-don too easy to beat, user-tested);
/// tracetype/traceprob and the pattern nodes' exact use are unknown.
/// </remarks>
public sealed class GhostTimingModel(GhostLevel level)
{
    // Fewer of the player's hits than this: the level's own spread.
    private const int MinimumSamples = 8;
    private readonly Queue<double> _samples = new();

    public GhostLevel Level { get; } = level;

    /// <summary>One of the player's hits, its offset from the note (late positive).</summary>
    public void Observe(TimeSpan offset)
    {
        _samples.Enqueue(offset.TotalMilliseconds);
        while (_samples.Count > Math.Max(1, Level.SampleSize))
            _samples.Dequeue();
    }

    /// <summary>The player's median offset and variance (ms, ms²) as AI-don uses them.</summary>
    public (double Median, double Variance) Statistics
    {
        get
        {
            if (_samples.Count < MinimumSamples)
                return (0, Level.VarianceFirst);
            var sorted = _samples.Order().ToArray();
            var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            var mean = sorted.Average();
            var variance = sorted.Sum(value => (value - mean) * (value - mean)) / sorted.Length;
            return (median, variance);
        }
    }

    /// <summary>The pattern for AI-don's lead, in 良 (negative: behind).</summary>
    public GhostPattern PatternFor(double lead) =>
        Level.Patterns.Count < 3 ? default : lead <= -1 ? Level.Patterns[1] : lead >= 1 ? Level.Patterns[2] : Level.Patterns[0];

    /// <summary>AI-don's offset for its next hit.</summary>
    public TimeSpan NextOffset(double lead, Random random)
    {
        var (median, variance) = Statistics;
        var scale = Math.Sqrt(Math.Max(0.05, 1 + PatternFor(lead).VariancePercent / 100.0));
        if (_samples.Count >= MinimumSamples)
            return TimeSpan.FromMilliseconds(median + (_samples.ElementAt(random.Next(_samples.Count)) - median) * scale);
        // Box-Muller: a standard normal draw.
        var normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        return TimeSpan.FromMilliseconds(median + Math.Sqrt(variance) * scale * normal);
    }
}
