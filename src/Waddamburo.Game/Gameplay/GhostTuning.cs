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
    int ScoreBad)
{
    /// <summary>emotionofs_center / threslow / threshigh: how far AI-don's mood moves the lead its spread follows
    /// (neutral, passive: as if ahead by this, aggressive: as if behind by this).</summary>
    public (int Center, int Low, int High) EmotionOffsets { get; init; } = (0, 2, 6);
}

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
                value(element, "hitscore_fuka"))
            {
                EmotionOffsets = (value(element, "emotionofs_center"), value(element, "emotionofs_threslow"), value(element, "emotionofs_threshigh")),
            };
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
/// ponytail: the rubber band is a reading of ghosttuning.xml, not traced: the spread moves from the first pattern's
/// toward the second's as AI-don falls behind (all of it at the first pattern node's inferior lead) and toward the
/// third's as it gets ahead (at its superior); the mood offsets the lead. defvariance_second is unused (as a floor it
/// made AI-don too easy to beat, user-tested); tracetype/traceprob and the other pattern nodes are unknown.
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
        _statistics = null;
    }

    // Sorted once per observed hit, not once per note AI-don decides (several in one frame cost ms on a slow PC).
    private (double Median, double Variance)? _statistics;

    /// <summary>
    /// The player's timing from their earlier plays (Green's GhostPerfData: median ms, variance ms²), used until this play
    /// has enough hits of its own; null: the level's own spread.
    /// </summary>
    public (double Median, double Variance)? Prior { get; init; }

    /// <summary>The player's median offset and variance (ms, ms²) as AI-don uses them.</summary>
    public (double Median, double Variance) Statistics =>
        _samples.Count < MinimumSamples ? Prior ?? (0, Level.VarianceFirst) : _statistics ??= TimingOf(_samples)!.Value;

    /// <summary>Hit offsets' median and variance (ms, ms²); null without any.</summary>
    public static (double Median, double Variance)? TimingOf(IEnumerable<double> offsets)
    {
        var sorted = offsets.Order().ToArray();
        if (sorted.Length == 0)
            return null;
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var mean = sorted.Average();
        return (median, sorted.Sum(value => (value - mean) * (value - mean)) / sorted.Length);
    }

    /// <summary>AI-don's mood, as ai_hit_effect's ChangeAIEmotion takes it: 0 more passive .. 2 neutral .. 4 more aggressive.</summary>
    public int Emotion { get; set; } = 2;

    /// <summary>How much AI-don's spread grows (percent; negative: tighter) at this lead, in territory points (the
    /// needle's units, negative: behind), after its mood's offset.</summary>
    public double VariancePercent(double lead)
    {
        if (Level.Patterns.Count < 3)
            return 0;
        var (inferior, superior) = Level.PatternNodes.Count > 0 ? Level.PatternNodes[0] : (-8, 4);
        var (center, low, high) = Level.EmotionOffsets;
        lead += Emotion >= 4 ? -high : Emotion <= 0 ? low : center;
        double baseline = Level.Patterns[0].VariancePercent;
        return lead < 0 && inferior < 0
            ? baseline + (Level.Patterns[1].VariancePercent - baseline) * Math.Min(1, lead / inferior)
            : lead > 0 && superior > 0
                ? baseline + (Level.Patterns[2].VariancePercent - baseline) * Math.Min(1, lead / superior)
                : baseline;
    }

    /// <summary>AI-don's offset for its next hit.</summary>
    public TimeSpan NextOffset(double lead, Random random)
    {
        var (median, variance) = Statistics;
        var scale = Math.Sqrt(Math.Max(0.05, 1 + VariancePercent(lead) / 100.0));
        if (_samples.Count >= MinimumSamples)
            return TimeSpan.FromMilliseconds(median + (_samples.ElementAt(random.Next(_samples.Count)) - median) * scale);
        // Box-Muller: a standard normal draw.
        var normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        return TimeSpan.FromMilliseconds(median + Math.Sqrt(variance) * scale * normal);
    }
}
