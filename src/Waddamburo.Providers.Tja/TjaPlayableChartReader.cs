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
        var commands = new List<(int Position, string Line)>();
        var balloons = new Queue<int>();
        var branchBalloons = new Dictionary<string, Queue<int>>();
        var longNotes = ImmutableArray.CreateBuilder<PlayableLongNote>();
        (TimeSpan Start, PlayableLongNoteKind Kind, int Hits)? pendingLong = null;
        var branch = false;
        var selectedRoute = true;
        var sawSelectedRoute = false;
        var routeToPlay = "#N";
        var skippedKusudama = false;
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

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = TjaMetadataReader.StripComment(lines[lineIndex]).Trim();
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

                var headerSeparator = line.IndexOf(':');
                var header = headerSeparator < 0 ? string.Empty : line[..headerSeparator].Trim().ToUpperInvariant();
                if (header == "COURSE" && course.Length != 0)
                {
                    balloons.Clear();
                    branchBalloons.Clear();
                }
                if (header is "BALLOON" or "BALLOONNOR" or "BALLOONEXP" or "BALLOONMAS")
                {
                    var values = balloons;
                    if (header != "BALLOON")
                    {
                        var route = header switch { "BALLOONNOR" => "#N", "BALLOONEXP" => "#E", _ => "#M" };
                        branchBalloons[route] = values = new Queue<int>();
                    }
                    values.Clear();
                    foreach (var value in line[(headerSeparator + 1)..].Split(','))
                        values.Enqueue(int.TryParse(value.Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var hits) && hits > 0 ? hits : 1);
                }
                readHeader(line, ref course, ref bpmText, ref offsetText);
                continue;
            }

            if (line.Equals("#END", StringComparison.OrdinalIgnoreCase))
            {
                if (measureData.Length != 0)
                    throw new InvalidDataException("The selected TJA chart ends with an unterminated measure.");
                if (branch && !sawSelectedRoute)
                    throw new InvalidDataException("TJA branch has no selected route.");
                foreach (var command in commands)
                    applyCommand(command.Line, ref numerator, ref denominator, ref bpm, ref scroll,
                        ref isGoGo, ref isBarlineVisible, ref cursorSeconds, timingPoints, scrollPoints, effectPoints);
                closeLong(chartTime(cursorSeconds));
                return new PlayableChart(
                    key,
                    authoredOffset(offsetText),
                    chartTime(cursorSeconds),
                    hitObjects.ToImmutable(),
                    timingPoints.ToImmutable(),
                    scrollPoints.ToImmutable(),
                    effectPoints.ToImmutable(),
                    barLines.ToImmutable(), longNotes.ToImmutable());
            }

            var commandName = line.Split([' ', '\t'], 2)[0].ToUpperInvariant();
            if (commandName == "#BRANCHSTART")
            {
                if ((branch && !sawSelectedRoute) || measureData.Length != 0)
                    throw new InvalidDataException("TJA branch must begin between measures and contain a selected route.");
                branch = true;
                selectedRoute = false;
                routeToPlay = string.Empty;
                for (var lookahead = lineIndex + 1; lookahead < lines.Length; lookahead++)
                {
                    var marker = TjaMetadataReader.StripComment(lines[lookahead]).Trim().ToUpperInvariant();
                    if (marker.StartsWith("#BRANCHSTART", StringComparison.Ordinal)
                        || marker is "#BRANCHEND" or "#END") break;
                    if (marker == "#N")
                    {
                        routeToPlay = marker;
                        break;
                    }
                    if (routeToPlay.Length == 0 && marker is "#E" or "#M")
                        routeToPlay = marker;
                }
                sawSelectedRoute = routeToPlay.Length == 0; // Empty branch blocks are legal.
                continue;
            }
            if (commandName is "#N" or "#E" or "#M")
            {
                if (!branch) throw new InvalidDataException("TJA route outside a branch.");
                if (measureData.Length != 0)
                    throw new InvalidDataException("TJA route ends with an unterminated measure.");
                selectedRoute = commandName == routeToPlay;
                skippedKusudama = false;
                sawSelectedRoute |= selectedRoute;
                continue;
            }
            if (commandName == "#BRANCHEND")
            {
                if (!branch || !sawSelectedRoute) throw new InvalidDataException("TJA branch has no selected route.");
                if (measureData.Length != 0)
                    throw new InvalidDataException("TJA route ends with an unterminated measure.");
                branch = false;
                selectedRoute = true;
                continue;
            }
            if (!selectedRoute)
            {
                // BALLOON is ordered by file appearance, including unused branch paths.
                if (line[0] != '#' && branchBalloons.Count == 0)
                    foreach (var symbol in line)
                    {
                        if (symbol == '9' && skippedKusudama) { skippedKusudama = false; continue; }
                        if (symbol is '7' or '9') balloons.TryDequeue(out _);
                        if (symbol is >= '5' and <= '9') skippedKusudama = symbol == '9';
                    }
                continue;
            }
            if (line[0] == '#')
            {
                commands.Add((measureData.Length, line));
                continue;
            }

            foreach (var character in line.Where(static character => !char.IsWhiteSpace(character)))
            {
                if (character != ',')
                {
                    measureData.Append(character);
                    continue;
                }
                if (++measureCount > maximumMeasures)
                    throw new InvalidDataException("The TJA chart exceeds the configured measure limit.");
                var count = Math.Max(1, measureData.Length);
                var commandIndex = 0;
                for (var index = 0; index <= count; index++)
                {
                    while (commandIndex < commands.Count && commands[commandIndex].Position == index)
                    {
                        applyCommand(commands[commandIndex++].Line, ref numerator, ref denominator,
                            ref bpm, ref scroll, ref isGoGo, ref isBarlineVisible, ref cursorSeconds,
                            timingPoints, scrollPoints, effectPoints);
                    }
                    if (index == count) break;
                    if (index == 0) barLines.Add(new ChartBarLine(chartTime(cursorSeconds), isBarlineVisible));
                    var symbol = measureData.Length == 0 ? '0' : char.ToUpperInvariant(measureData[index]);
                    var time = chartTime(cursorSeconds);
                    if (symbol is '5' or '6' or '7' or '9')
                    {
                        var closesKusudama = symbol == '9' && pendingLong?.Kind == PlayableLongNoteKind.Kusudama;
                        closeLong(time);
                        var kind = symbol switch
                        {
                            '5' => PlayableLongNoteKind.Roll,
                            '6' => PlayableLongNoteKind.BigRoll,
                            '7' => PlayableLongNoteKind.Balloon,
                            _ => PlayableLongNoteKind.Kusudama,
                        };
                        if (!closesKusudama)
                        {
                            var hits = 0;
                            if (symbol is '7' or '9')
                            {
                                var values = branch && branchBalloons.Count != 0
                                    ? branchBalloons.GetValueOrDefault(routeToPlay) : balloons;
                                hits = values is not null && values.TryDequeue(out var requiredHits) ? requiredHits : 1;
                            }
                            pendingLong = (time, kind, hits);
                        }
                    }
                    else if (symbol == '8') closeLong(time);
                    else
                    {
                        var kind = symbol switch
                        {
                            '0' => (PlayableNoteKind?)null,
                            '1' => PlayableNoteKind.Don,
                            '2' => PlayableNoteKind.Ka,
                            '3' or 'A' => PlayableNoteKind.BigDon,
                            '4' or 'B' => PlayableNoteKind.BigKa,
                            _ => throw new NotSupportedException($"TJA note '{symbol}' is not supported (gimmick or invalid note)."),
                        };
                        if (kind is { } value)
                        {
                            checkNoteLimit();
                            hitObjects.Add(new PlayableHitObject(time, value));
                        }
                    }
                    cursorSeconds = checked(cursorSeconds + 60m / bpm * 4m * numerator / denominator / count);
                    ensureTimeFits(cursorSeconds, "The TJA chart exceeds the supported time range.");
                }
                measureData.Clear();
                commands.Clear();
            }
        }

        void checkNoteLimit()
        {
            if (hitObjects.Count + longNotes.Count >= maximumNotes)
                throw new InvalidDataException("The TJA chart exceeds the configured note limit.");
        }

        void closeLong(TimeSpan end)
        {
            if (pendingLong is not { } note) return;
            checkNoteLimit();
            longNotes.Add(new PlayableLongNote(note.Start, end, note.Kind, note.Hits));
            pendingLong = null;
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
            case "#SECTION":
            case "#LEVELHOLD":
            case "#SENOTECHANGE":
            case "#LYRIC":
                // Fixed Normal route; lyric/sound-decoration commands do not alter timing.
                break;
            default:
                throw new NotSupportedException($"TJA command '{name}' is not supported by the gameplay loader.");
        }
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
