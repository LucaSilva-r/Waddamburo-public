using System.Collections.Immutable;

namespace Waddamburo.Catalog;

/// <summary>
/// What a chart draws that <see cref="ChartHash"/> leaves out (it changes no judgement or score): complex scroll,
/// #SUDDEN, scrolling by beat, the target's moves, forced note labels, each 譜面分岐 route's own tempo and
/// scroll, and modchart changes (#EASE/#SET). A song package carries it beside the canonical bytes so a guest sees the chart as the host does.
/// </summary>
public static class ChartDrawing
{
    private const byte Version = 2;
    private const int MaxCount = 1 << 20;
    private static readonly string[] Labels = ["don", "do", "ko", "katsu", "ka", "don_dai", "katsu_dai"];

    public static byte[] Serialize(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("WDBD"u8);
        writer.Write(Version);
        writeScroll(writer, chart.ScrollPoints);
        writer.Write(chart.Effects.JudgeMoves.Length);
        foreach (var move in chart.Effects.JudgeMoves)
        {
            writer.Write(move.Time.Ticks);
            writer.Write(move.Duration.Ticks);
            writer.Write(move.X);
            writer.Write(move.Y);
        }
        writer.Write(chart.Effects.Beats.Length);
        foreach (var point in chart.Effects.Beats)
        {
            writer.Write(point.Time.Ticks);
            writer.Write(point.Beat);
            writer.Write(point.BeatsPerSecond);
        }
        writer.Write(chart.HitObjects.Length);
        foreach (var note in chart.HitObjects)
            writer.Write((byte)(note.Text is { } text ? Array.IndexOf(Labels, text) + 1 : 0));
        writer.Write(chart.RouteControls.Length);
        foreach (var route in chart.RouteControls)
        {
            writer.Write(route.Timing.Length);
            foreach (var point in route.Timing)
            {
                writer.Write(point.Time.Ticks);
                writer.Write(point.BeatsPerMinute);
                writer.Write(point.BeatsPerMeasure);
                writer.Write(point.BeatUnit);
            }
            writeScroll(writer, route.Scroll);
        }
        writer.Write(chart.Effects.Mods.Length);
        foreach (var change in chart.Effects.Mods)
        {
            writer.Write(change.Start.Ticks);
            writer.Write(change.Length.Ticks);
            writer.Write(change.Target);
            writer.Write(change.Property);
            writer.Write(change.Value);
            writer.Write((byte)change.Ease);
            writer.Write(change.Initial);
            writer.Write(change.Curve.HasValue);
            if (change.Curve is { } c)
            {
                if (!c.IsValid) throw new InvalidDataException("Invalid Bézier handles.");
                writer.Write(c.X1); writer.Write(c.Y1); writer.Write(c.X2); writer.Write(c.Y2);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// <paramref name="chart"/> (canonical, from <see cref="ChartHash.Deserialize"/>) with this drawing data back; throws
    /// <see cref="InvalidDataException"/> or <see cref="ArgumentException"/> when the bytes don't fit it.
    /// </summary>
    public static PlayableChart Apply(PlayableChart chart, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(data);
        using var reader = new BinaryReader(new MemoryStream(data));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("WDBD"u8))
            throw new InvalidDataException("Not chart drawing data of this version.");
        var version = reader.ReadByte();
        if (version is < 1 or > Version) throw new InvalidDataException("Unknown chart drawing version.");
        var scroll = readScroll(reader);
        var moves = new ChartJudgeMove[count(reader)];
        for (var index = 0; index < moves.Length; index++)
            moves[index] = new ChartJudgeMove(time(reader), time(reader), finite(reader), finite(reader));
        var beats = new ChartBeatPoint[count(reader)];
        for (var index = 0; index < beats.Length; index++)
            beats[index] = new ChartBeatPoint(time(reader), finite(reader), finite(reader));
        if (count(reader) != chart.HitObjects.Length)
            throw new InvalidDataException("Chart drawing data is for another chart's notes.");
        var notes = chart.HitObjects.ToBuilder();
        for (var index = 0; index < notes.Count; index++)
            notes[index] = notes[index] with
            {
                Text = reader.ReadByte() is var label and > 0 ? (label <= Labels.Length ? Labels[label - 1]
                    : throw new InvalidDataException("Unknown note label.")) : null,
            };
        var routes = new ChartRouteControls[count(reader)];
        // The canonical bytes carry routes only when one has its own Go-Go or bar lines; else they come from here alone.
        if (routes.Length is not (0 or 3) || !chart.RouteControls.IsEmpty && routes.Length != chart.RouteControls.Length)
            throw new InvalidDataException("Chart drawing data has another chart's routes.");
        for (var route = 0; route < routes.Length; route++)
        {
            var timing = new ChartTimingPoint[count(reader)];
            for (var index = 0; index < timing.Length; index++)
                timing[index] = new ChartTimingPoint(time(reader), finite(reader), reader.ReadInt32(), reader.ReadInt32());
            var scrolls = readScroll(reader);
            routes[route] = chart.RouteControls.IsEmpty ? new ChartRouteControls(ordered(timing), scrolls)
                : chart.RouteControls[route] with { Timing = ordered(timing), Scroll = scrolls };
        }
        var mods = new ChartModEase[count(reader)];
        for (var index = 0; index < mods.Length; index++)
        {
            var (start, length, target, property, value, ease) =
                (time(reader), time(reader), reader.ReadString(), reader.ReadString(), finite(reader), (ChartEase)reader.ReadByte());
            if (!ChartMods.IsKnown(target, property) || !Enum.IsDefined(ease))
                throw new InvalidDataException("Unknown modchart target, property or easing.");
            var initial = version >= 2 && reader.ReadBoolean();
            ChartModCurve? curve = version >= 2 && reader.ReadBoolean()
                ? new ChartModCurve(finite(reader), finite(reader), finite(reader), finite(reader)) : null;
            if (curve is { IsValid: false }) throw new InvalidDataException("Invalid Bézier handles.");
            mods[index] = new ChartModEase(start, length, target, property, value, ease, curve, initial);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Trailing bytes after the chart drawing data.");
        return chart.WithScrollPoints(scroll).WithHitObjects(notes.ToImmutable()) with
        {
            Effects = new ChartEffects { JudgeMoves = ordered(moves), Beats = ordered(beats), Mods = ordered(mods) },
            RouteControls = [.. routes],
        };
    }

    private static void writeScroll(BinaryWriter writer, ImmutableArray<ChartScrollPoint> points)
    {
        writer.Write(points.Length);
        foreach (var point in points)
        {
            writer.Write(point.Time.Ticks);
            writer.Write(point.Multiplier);
            writer.Write(point.Imaginary);
            writer.Write(point.Show?.Ticks ?? -1);
            writer.Write(point.Wait?.Ticks ?? -1);
            writer.Write(point.ByBeat);
        }
    }

    private static ImmutableArray<ChartScrollPoint> readScroll(BinaryReader reader)
    {
        var points = new ChartScrollPoint[count(reader)];
        if (points.Length == 0)
            throw new InvalidDataException("A chart needs a scroll point.");
        for (var index = 0; index < points.Length; index++)
            points[index] = new ChartScrollPoint(time(reader), finite(reader))
            {
                Imaginary = finite(reader),
                Show = optionalTime(reader),
                Wait = optionalTime(reader),
                ByBeat = reader.ReadBoolean(),
            };
        return ordered(points);
    }

    private static ImmutableArray<T> ordered<T>(T[] points) where T : struct
    {
        for (var index = 1; index < points.Length; index++)
            if (timeOf(points[index]) < timeOf(points[index - 1]))
                throw new InvalidDataException("Chart drawing points are out of order.");
        return [.. points];
    }

    private static TimeSpan timeOf<T>(T point) => point switch
    {
        ChartScrollPoint value => value.Time,
        ChartTimingPoint value => value.Time,
        ChartJudgeMove value => value.Time,
        ChartBeatPoint value => value.Time,
        ChartModEase value => value.Start,
        _ => throw new InvalidOperationException(),
    };

    private static int count(BinaryReader reader) =>
        reader.ReadInt32() is var value and >= 0 and <= MaxCount ? value : throw new InvalidDataException("Bad count.");

    private static TimeSpan time(BinaryReader reader) =>
        reader.ReadInt64() is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : throw new InvalidDataException("Negative time.");

    private static TimeSpan? optionalTime(BinaryReader reader) =>
        reader.ReadInt64() is var ticks && ticks == -1 ? null
            : ticks >= 0 ? TimeSpan.FromTicks(ticks) : throw new InvalidDataException("Negative time.");

    private static double finite(BinaryReader reader) =>
        reader.ReadDouble() is var value && double.IsFinite(value) ? value : throw new InvalidDataException("Not a finite number.");
}
