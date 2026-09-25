using System.Security.Cryptography;

namespace Waddamburo.Catalog;

/// <summary>
/// A chart's identity for scores: SHA-256 of its canonical playable form (everything that changes
/// how the chart plays), independent of the source file, its format and its metadata. The same
/// bytes are what a score server imports to rescore plays.
/// </summary>
public static class ChartHash
{
    /// <summary>Bump when the serialization changes; every chart then gets a new identity.</summary>
    public const byte FormatVersion = 1;

    public static string Compute(PlayableChart chart, TaikoCourse course) =>
        Convert.ToHexStringLower(SHA256.HashData(Serialize(chart, course)));

    /// <summary>Little-endian; times in whole microseconds so float noise below that cannot split charts.</summary>
    public static byte[] Serialize(PlayableChart chart, TaikoCourse course)
    {
        ArgumentNullException.ThrowIfNull(chart);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("WDBC"u8);
        writer.Write(FormatVersion);
        writer.Write((byte)course);
        writer.Write(chart.Level ?? -1);
        writer.Write(chart.ScoreInit ?? -1);
        writer.Write(chart.ScoreDiff ?? -1);
        writer.Write(micros(chart.AuthoredOffset));
        writer.Write(chart.HitObjects.Length);
        foreach (var note in chart.HitObjects)
        {
            writer.Write(micros(note.StartTime));
            writer.Write((byte)note.Kind);
            writer.Write((byte)((note.IsHand ? 1 : 0) | (note.IsSynchro ? 2 : 0) | (note.InRun ? 4 : 0) | (note.IsRare ? 8 : 0)));
        }
        writer.Write(chart.LongNotes.Length);
        foreach (var note in chart.LongNotes)
        {
            writer.Write(micros(note.StartTime));
            writer.Write(micros(note.EndTime));
            writer.Write((byte)note.Kind);
            writer.Write(note.RequiredHits);
        }
        writer.Write(chart.TimingPoints.Length);
        foreach (var point in chart.TimingPoints)
        {
            writer.Write(micros(point.Time));
            writer.Write(point.BeatsPerMinute);
            writer.Write(point.BeatsPerMeasure);
            writer.Write(point.BeatUnit);
        }
        writer.Write(chart.ScrollPoints.Length);
        foreach (var point in chart.ScrollPoints)
        {
            writer.Write(micros(point.Time));
            writer.Write(point.Multiplier);
        }
        writer.Write(chart.EffectPoints.Length);
        foreach (var point in chart.EffectPoints)
        {
            writer.Write(micros(point.Time));
            writer.Write(point.IsGoGo);
        }
        writer.Write(chart.BarLines.Length);
        foreach (var line in chart.BarLines)
        {
            writer.Write(micros(line.Time));
            writer.Write(line.IsVisible);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static long micros(TimeSpan time) => (long)Math.Round(time.Ticks / 10d, MidpointRounding.AwayFromZero);
}
