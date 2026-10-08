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

    /// <summary>
    /// Reads <see cref="Serialize"/>'s bytes back (a score server's stored chart, to rescore plays).
    /// The chart has no source file: its key names <paramref name="source"/> only, its duration ends at
    /// its last event. Times come back whole microseconds, so a re-serialization gives the same hash.
    /// </summary>
    public static (PlayableChart Chart, TaikoCourse Course) Deserialize(byte[] data, SongSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var reader = new BinaryReader(new MemoryStream(data));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("WDBC"u8) || reader.ReadByte() != FormatVersion)
            throw new InvalidDataException("Not a canonical chart of this version.");
        var course = (TaikoCourse)reader.ReadByte();
        var level = reader.ReadInt32();
        var scoreInit = reader.ReadInt32();
        var scoreDiff = reader.ReadInt32();
        var offset = time(reader);
        var notes = new PlayableHitObject[count(reader)];
        for (var index = 0; index < notes.Length; index++)
        {
            var start = time(reader);
            var kind = (PlayableNoteKind)reader.ReadByte();
            var flags = reader.ReadByte();
            notes[index] = new PlayableHitObject(start, kind, (flags & 1) != 0)
            {
                IsSynchro = (flags & 2) != 0,
                InRun = (flags & 4) != 0,
                IsRare = (flags & 8) != 0,
            };
        }
        var longNotes = new PlayableLongNote[count(reader)];
        for (var index = 0; index < longNotes.Length; index++)
            longNotes[index] = new PlayableLongNote(time(reader), time(reader), (PlayableLongNoteKind)reader.ReadByte(), reader.ReadInt32());
        var timing = new ChartTimingPoint[count(reader)];
        for (var index = 0; index < timing.Length; index++)
            timing[index] = new ChartTimingPoint(time(reader), reader.ReadDouble(), reader.ReadInt32(), reader.ReadInt32());
        var scroll = new ChartScrollPoint[count(reader)];
        for (var index = 0; index < scroll.Length; index++)
            scroll[index] = new ChartScrollPoint(time(reader), reader.ReadDouble());
        var effects = new ChartEffectPoint[count(reader)];
        for (var index = 0; index < effects.Length; index++)
            effects[index] = new ChartEffectPoint(time(reader), reader.ReadBoolean());
        var bars = new ChartBarLine[count(reader)];
        for (var index = 0; index < bars.Length; index++)
            bars[index] = new ChartBarLine(time(reader), reader.ReadBoolean());
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Trailing bytes after the chart.");

        var duration = notes.Select(static note => note.StartTime).Concat(longNotes.Select(static note => note.EndTime))
            .Concat(bars.Select(static line => line.Time)).DefaultIfEmpty(TimeSpan.Zero).Max();
        var chart = new PlayableChart(new ChartKey(new SongKey(source, "rescore"), "rescore"), offset, duration,
            notes, timing, scroll, effects, bars, longNotes)
        {
            Level = level < 0 ? null : level,
            ScoreInit = scoreInit < 0 ? null : scoreInit,
            ScoreDiff = scoreDiff < 0 ? null : scoreDiff,
        };
        return (chart, course);

        static TimeSpan time(BinaryReader reader) => TimeSpan.FromTicks(checked(reader.ReadInt64() * 10));

        static int count(BinaryReader reader)
        {
            var value = reader.ReadInt32();
            // Every entry takes at least 9 bytes: a corrupt count must not allocate gigabytes.
            if (value < 0 || value > (reader.BaseStream.Length - reader.BaseStream.Position) / 9)
                throw new InvalidDataException("Bad chart entry count.");
            return value;
        }
    }

    private static long micros(TimeSpan time) => (long)Math.Round(time.Ticks / 10d, MidpointRounding.AwayFromZero);
}
