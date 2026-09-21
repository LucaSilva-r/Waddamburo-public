using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

internal static class TjaPlayableChartReader
{
    private const decimal TicksPerSecond = TimeSpan.TicksPerSecond;

    public static PlayableChart Read(
        ReadOnlySpan<byte> bytes,
        ChartKey key,
        TjaChartLocator target,
        int maximumMeasures,
        int maximumNotes)
    {
        var text = TjaMetadataReader.Decode(bytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var course = string.Empty;
        var bpmText = string.Empty;
        var offsetText = string.Empty;
        var occurrences = new Dictionary<(TaikoCourse Course, string Player), int>();
        var active = false;
        var measureData = new StringBuilder();
        var hitObjects = ImmutableArray.CreateBuilder<PlayableHitObject>();
        var timingPoints = ImmutableArray.CreateBuilder<ChartTimingPoint>();
        var scrollPoints = ImmutableArray.CreateBuilder<ChartScrollPoint>();
        var effectPoints = ImmutableArray.CreateBuilder<ChartEffectPoint>();
        var barLines = ImmutableArray.CreateBuilder<ChartBarLine>();
        var measureCount = 0;
        var numerator = 4;
        var denominator = 4;
        var bpm = 0m;
        var scroll = 1d;
        var isGoGo = false;
        var isBarlineVisible = true;
        var cursorSeconds = 0m;

        foreach (var rawLine in lines)
        {
            var line = TjaMetadataReader.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (!active)
            {
                if (line.StartsWith("#START", StringComparison.OrdinalIgnoreCase))
                {
                    var player = normalizePlayer(line.AsSpan(6));
                    if (!TjaMetadataReader.TryParseCourse(course, out var parsedCourse))
                        continue;
                    var occurrenceKey = (parsedCourse, player);
                    occurrences.TryGetValue(occurrenceKey, out var occurrence);
                    occurrences[occurrenceKey] = ++occurrence;
                    if (parsedCourse == target.Course
                        && player == target.Player
                        && occurrence == target.Occurrence)
                    {
                        bpm = positiveNumber(bpmText, "BPM");
                        timingPoints.Add(new ChartTimingPoint(TimeSpan.Zero, (double)bpm, numerator, denominator));
                        scrollPoints.Add(new ChartScrollPoint(TimeSpan.Zero, scroll));
                        effectPoints.Add(new ChartEffectPoint(TimeSpan.Zero, isGoGo));
                        active = true;
                    }
                    continue;
                }

                readHeader(line, ref course, ref bpmText, ref offsetText);
                continue;
            }

            if (line.Equals("#END", StringComparison.OrdinalIgnoreCase))
            {
                if (measureData.Length != 0)
                    throw new InvalidDataException("The selected TJA chart ends with an unterminated measure.");
                return new PlayableChart(
                    key,
                    authoredOffset(offsetText),
                    chartTime(cursorSeconds),
                    hitObjects.ToImmutable(),
                    timingPoints.ToImmutable(),
                    scrollPoints.ToImmutable(),
                    effectPoints.ToImmutable(),
                    barLines.ToImmutable());
            }

            if (line[0] == '#')
            {
                if (measureData.Length != 0)
                    throw new InvalidDataException("The initial TJA gameplay loader supports commands only between measures.");
                applyCommand(line, ref numerator, ref denominator, ref bpm, ref scroll, ref isGoGo,
                    ref isBarlineVisible, ref cursorSeconds, timingPoints, scrollPoints, effectPoints);
                continue;
            }

            foreach (var character in line.Where(static character => !char.IsWhiteSpace(character)))
            {
                if (character == ',')
                {
                    appendMeasure(measureData, hitObjects, barLines, numerator, denominator, bpm,
                        isBarlineVisible, ref cursorSeconds, ref measureCount, maximumMeasures, maximumNotes);
                }
                else
                    measureData.Append(character);
            }
        }

        throw new InvalidDataException(active
            ? "The selected TJA chart has no #END marker."
            : "The selected TJA chart section no longer exists.");
    }

    private static void readHeader(string line, ref string course, ref string bpm, ref string offset)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
            return;
        var name = line[..colon].Trim();
        var value = line[(colon + 1)..].Trim();
        if (name.Equals("COURSE", StringComparison.OrdinalIgnoreCase))
            course = value;
        else if (name.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            bpm = value;
        else if (name.Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
            offset = value;
    }

    private static void applyCommand(
        string line,
        ref int numerator,
        ref int denominator,
        ref decimal bpm,
        ref double scroll,
        ref bool isGoGo,
        ref bool isBarlineVisible,
        ref decimal cursorSeconds,
        ImmutableArray<ChartTimingPoint>.Builder timingPoints,
        ImmutableArray<ChartScrollPoint>.Builder scrollPoints,
        ImmutableArray<ChartEffectPoint>.Builder effectPoints)
    {
        var separator = line.IndexOfAny([' ', '\t']);
        var name = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
        var argument = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
        switch (name)
        {
            case "#MEASURE":
                var parts = argument.Split('/');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out numerator)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator)
                    || numerator <= 0
                    || denominator <= 0)
                {
                    throw new InvalidDataException("TJA #MEASURE requires positive integer numerator/denominator values.");
                }
                appendOrReplace(timingPoints, new ChartTimingPoint(chartTime(cursorSeconds), (double)bpm, numerator, denominator));
                break;
            case "#BPMCHANGE":
                bpm = positiveNumber(argument, "#BPMCHANGE");
                appendOrReplace(timingPoints, new ChartTimingPoint(chartTime(cursorSeconds), (double)bpm, numerator, denominator));
                break;
            case "#SCROLL":
                scroll = finiteDouble(argument, "#SCROLL");
                appendOrReplace(scrollPoints, new ChartScrollPoint(chartTime(cursorSeconds), scroll));
                break;
            case "#DELAY":
                var delaySeconds = finiteDecimal(argument, "#DELAY");
                if (delaySeconds < 0)
                    throw new InvalidDataException("TJA #DELAY must be non-negative.");
                cursorSeconds = checked(cursorSeconds + delaySeconds);
                ensureTimeFits(cursorSeconds, "TJA #DELAY places the chart outside the supported time range.");
                break;
            case "#GOGOSTART":
                isGoGo = true;
                appendOrReplace(effectPoints, new ChartEffectPoint(chartTime(cursorSeconds), true));
                break;
            case "#GOGOEND":
                isGoGo = false;
                appendOrReplace(effectPoints, new ChartEffectPoint(chartTime(cursorSeconds), false));
                break;
            case "#BARLINEON":
                isBarlineVisible = true;
                break;
            case "#BARLINEOFF":
                isBarlineVisible = false;
                break;
            default:
                throw new NotSupportedException($"TJA command '{name}' is not supported by the initial gameplay loader.");
        }
    }

    private static void appendMeasure(
        StringBuilder data,
        ImmutableArray<PlayableHitObject>.Builder hitObjects,
        ImmutableArray<ChartBarLine>.Builder barLines,
        int numerator,
        int denominator,
        decimal bpm,
        bool isBarlineVisible,
        ref decimal cursorSeconds,
        ref int measureCount,
        int maximumMeasures,
        int maximumNotes)
    {
        if (++measureCount > maximumMeasures)
            throw new InvalidDataException("The TJA chart exceeds the configured measure limit.");
        if (data.Length == 0)
            data.Append('0');
        var measureDuration = checked(60m / bpm * 4m * numerator / denominator);
        barLines.Add(new ChartBarLine(chartTime(cursorSeconds), isBarlineVisible));
        for (var index = 0; index < data.Length; index++)
        {
            var kind = data[index] switch
            {
                '0' => (PlayableNoteKind?)null,
                '1' => PlayableNoteKind.Don,
                '2' => PlayableNoteKind.Ka,
                '3' => PlayableNoteKind.BigDon,
                '4' => PlayableNoteKind.BigKa,
                >= '5' and <= '9' => throw new NotSupportedException(
                    $"TJA note type '{data[index]}' is not supported by the initial gameplay loader."),
                _ => throw new InvalidDataException($"Invalid TJA note character '{data[index]}'."),
            };
            if (kind is not { } noteKind)
                continue;
            if (hitObjects.Count >= maximumNotes)
                throw new InvalidDataException("The TJA chart exceeds the configured note limit.");
            var noteSeconds = checked(cursorSeconds + measureDuration * index / data.Length);
            hitObjects.Add(new PlayableHitObject(chartTime(noteSeconds), noteKind));
        }
        cursorSeconds = checked(cursorSeconds + measureDuration);
        ensureTimeFits(cursorSeconds, "The TJA chart exceeds the supported time range.");
        data.Clear();
    }

    private static void appendOrReplace<T>(ImmutableArray<T>.Builder points, T point)
        where T : struct
    {
        var time = point switch
        {
            ChartTimingPoint value => value.Time,
            ChartScrollPoint value => value.Time,
            ChartEffectPoint value => value.Time,
            _ => throw new InvalidOperationException("Unsupported control point type."),
        };
        var previousTime = points[^1] switch
        {
            ChartTimingPoint value => value.Time,
            ChartScrollPoint value => value.Time,
            ChartEffectPoint value => value.Time,
            _ => throw new InvalidOperationException("Unsupported control point type."),
        };
        if (previousTime == time)
            points[^1] = point;
        else
            points.Add(point);
    }

    private static string normalizePlayer(ReadOnlySpan<char> value) => value.Trim().ToString().ToUpperInvariant() switch
    {
        "1P" => "P1",
        "2P" => "P2",
        var player => player,
    };

    private static decimal positiveNumber(string value, string field)
    {
        var number = finiteDecimal(value, field);
        return number > 0 ? number : throw new InvalidDataException($"TJA {field} must be positive.");
    }

    private static decimal finiteDecimal(string value, string field) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidDataException($"TJA {field} must be a finite decimal number.");

    private static double finiteDouble(string value, string field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"TJA {field} must be a finite number.");

    private static TimeSpan authoredOffset(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.Zero;
        var seconds = finiteDecimal(value, "OFFSET");
        if (Math.Abs(seconds) > (decimal)TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidDataException("TJA OFFSET does not fit in a time span.");
        return TimeSpan.FromTicks(decimal.ToInt64(decimal.Round(seconds * TicksPerSecond)));
    }

    private static TimeSpan chartTime(decimal seconds)
    {
        ensureTimeFits(seconds, "The TJA chart exceeds the supported time range.");
        return TimeSpan.FromTicks(decimal.ToInt64(decimal.Round(seconds * TicksPerSecond)));
    }

    private static void ensureTimeFits(decimal seconds, string message)
    {
        if (seconds < 0 || seconds > (decimal)TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidDataException(message);
    }
}
