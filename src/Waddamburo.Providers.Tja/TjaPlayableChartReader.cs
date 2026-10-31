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
        (TimeSpan Start, PlayableLongNoteKind Kind, int Hits, ChartBranch? Branch)? pendingLong = null;
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
        var maxSeconds = 0m; // latest event time; the chart ends no earlier
        int? level = null;
        int? scoreInit = null;
        int? scoreDiff = null;

        // 譜面分岐: every route is read with its notes tagged. The main route (#N, else the first one written)
        // gives the state the chart goes on with after the block; the other routes' control points and bar lines
        // go to scratch streams, kept per block for RouteControls.
        // A route a block lacks plays the main one's notes.
        var branch = false;
        var mainRoute = "#N";
        string? route = null;
        var routesSeen = new HashSet<string>();
        TjaState atBranch = default, afterMain = default;
        (int Notes, int Longs) mainFrom = default, mainTo = default;
        var checks = new List<TjaCheck>();
        var levelHold = new HashSet<ChartBranch>();
        var routeBlocks = new List<TjaRouteBlock>();
        var ownRoutes = new Dictionary<string, TjaRouteStreams>();
        var timing = timingPoints;
        var scrolls = scrollPoints;
        var effects = effectPoints;
        var bars = barLines;
        bool onMain() => !branch || route == mainRoute;
        ChartBranch? tag() => branch && route is not null ? routeOf(route) : null;
        TjaState save() => new(numerator, denominator, bpm, scroll, isGoGo, isBarlineVisible, cursorSeconds, pendingLong);
        void restore(TjaState state) =>
            (numerator, denominator, bpm, scroll, isGoGo, isBarlineVisible, cursorSeconds, pendingLong) =
            (state.Numerator, state.Denominator, state.Bpm, state.Scroll, state.GoGo, state.Barline, state.Cursor, state.Pending);

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
                if (header == "LEVEL")
                    level = int.TryParse(line[(headerSeparator + 1)..].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var stars) && stars > 0 ? stars : null;
                if (header is "SCOREINIT" or "SCOREDIFF")
                {
                    // SCOREINIT may carry a second Shin-uchi term; Gen 3 uses the first.
                    var value = line[(headerSeparator + 1)..].Split(',', 2)[0].Trim();
                    int? parsed = int.TryParse(value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? amount : null;
                    if (header == "SCOREINIT") scoreInit = parsed;
                    else scoreDiff = parsed;
                }
                if (header == "COURSE" && course.Length != 0)
                {
                    balloons.Clear();
                    branchBalloons.Clear();
                    scoreInit = null;
                    scoreDiff = null;
                }
                if (header is "BALLOON" or "BALLOONNOR" or "BALLOONEXP" or "BALLOONMAS")
                {
                    var values = balloons;
                    if (header != "BALLOON")
                    {
                        var balloonRoute = header switch { "BALLOONNOR" => "#N", "BALLOONEXP" => "#E", _ => "#M" };
                        branchBalloons[balloonRoute] = values = new Queue<int>();
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
                foreach (var command in commands)
                    applyCommand(command.Line, ref numerator, ref denominator, ref bpm, ref scroll,
                        ref isGoGo, ref isBarlineVisible, ref cursorSeconds, timing, scrolls, effects);
                commands.Clear();
                closeBranch();
                closeLong(chartTime(cursorSeconds));
                var notes = hitObjects.OrderBy(static n => n.StartTime).ToImmutableArray();
                var longs = longNotes.OrderBy(static n => n.StartTime).ToImmutableArray();
                return new PlayableChart(
                    key,
                    authoredOffset(offsetText),
                    chartTime(Math.Max(cursorSeconds, maxSeconds)),
                    // A negative #DELAY can step back in time; OrderBy is stable, so ties keep file order.
                    notes,
                    timingPoints.ToImmutable(),
                    scrollPoints.ToImmutable(),
                    effectPoints.ToImmutable(),
                    barLines.OrderBy(static b => b.Time), longs)
                {
                    Level = level,
                    ScoreInit = scoreInit,
                    ScoreDiff = scoreDiff,
                    Branching = checks.Count == 0 ? null : branching(checks, notes, longs),
                    RouteControls = routeBlocks.Count == 0 ? [] : [.. ((string[])["#N", "#E", "#M"]).Select(name => new ChartRouteControls(
                        routeStream(timingPoints, routeBlocks, name, static own => own.Timing, static point => point.Time, static (point, at) => new ChartTimingPoint(at, point.BeatsPerMinute, point.BeatsPerMeasure, point.BeatUnit)),
                        routeStream(scrollPoints, routeBlocks, name, static own => own.Scroll, static point => point.Time, static (point, at) => new ChartScrollPoint(at, point.Multiplier)))
                    {
                        Effects = ownEffects(effectPoints, routeStream(effectPoints, routeBlocks, name, static own => own.Effects, static point => point.Time, static (point, at) => point.At(at))),
                        Bars = ownBars(barLines, routeBlocks, name),
                    })],
                };
            }

            var commandName = line.Split([' ', '\t'], 2)[0].ToUpperInvariant();
            if (commandName == "#BRANCHSTART")
            {
                if (measureData.Length != 0)
                    throw new InvalidDataException("TJA branch must begin between measures.");
                closeBranch();
                var start = chartTime(cursorSeconds);
                // Judged a measure before the branch, at the previous bar line (TJA: "calculated one measure
                // before #BRANCHSTART").
                var at = barLines.Count != 0 && barLines[^1].Time < start ? barLines[^1].Time : start;
                checks.Add(new TjaCheck(at, start, line[commandName.Length..].Trim(), [.. levelHold]));
                levelHold.Clear();
                branch = true;
                atBranch = afterMain = save();
                ownRoutes = [];
                routesSeen.Clear();
                route = null;
                mainRoute = string.Empty;
                for (var lookahead = lineIndex + 1; lookahead < lines.Length; lookahead++)
                {
                    var marker = TjaMetadataReader.StripComment(lines[lookahead]).Trim().ToUpperInvariant();
                    if (marker.StartsWith("#BRANCHSTART", StringComparison.Ordinal)
                        || marker is "#BRANCHEND" or "#END") break;
                    if (marker == "#N")
                    {
                        mainRoute = marker;
                        break;
                    }
                    if (mainRoute.Length == 0 && marker is "#E" or "#M")
                        mainRoute = marker;
                }
                continue;
            }
            if (commandName is "#N" or "#E" or "#M")
            {
                if (!branch) throw new InvalidDataException("TJA route outside a branch.");
                if (measureData.Length != 0)
                    throw new InvalidDataException("TJA route ends with an unterminated measure.");
                endRoute();
                restore(atBranch);
                route = commandName;
                routesSeen.Add(route);
                if (onMain())
                {
                    mainFrom = (hitObjects.Count, longNotes.Count);
                    (timing, scrolls, effects, bars) = (timingPoints, scrollPoints, effectPoints, barLines);
                }
                else
                {
                    // Seeded with the state at the branch, just before it: commands are placed after the last point,
                    // and the main route may already have gone past this route's.
                    var seed = TimeSpan.FromTicks(Math.Max(chartTime(atBranch.Cursor).Ticks - 1, 0));
                    timing = ImmutableArray.CreateBuilder<ChartTimingPoint>();
                    timing.Add(new ChartTimingPoint(seed, (double)atBranch.Bpm, atBranch.Numerator, atBranch.Denominator));
                    scrolls = ImmutableArray.CreateBuilder<ChartScrollPoint>();
                    scrolls.Add(new ChartScrollPoint(seed, atBranch.Scroll));
                    effects = ImmutableArray.CreateBuilder<ChartEffectPoint>();
                    effects.Add(new ChartEffectPoint(seed, atBranch.GoGo));
                    bars = ImmutableArray.CreateBuilder<ChartBarLine>();
                }
                continue;
            }
            if (commandName == "#BRANCHEND")
            {
                if (!branch) throw new InvalidDataException("TJA #BRANCHEND outside a branch.");
                if (measureData.Length != 0)
                    throw new InvalidDataException("TJA route ends with an unterminated measure.");
                closeBranch();
                continue;
            }
            if (commandName == "#LEVELHOLD")
            {
                levelHold.Add(tag() ?? ChartBranch.Normal);
                continue;
            }
            if (branch && route is null && line[0] != '#')
                throw new InvalidDataException("TJA branch notes come before any route.");
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
                            timing, scrolls, effects);
                    }
                    if (index == count) break;
                    if (index == 0) bars.Add(new ChartBarLine(chartTime(cursorSeconds), isBarlineVisible));
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
                                // BALLOON is ordered by file appearance, every route's balloons included.
                                var values = branch && branchBalloons.Count != 0
                                    ? branchBalloons.GetValueOrDefault(route ?? mainRoute) : balloons;
                                hits = values is not null && values.TryDequeue(out var requiredHits) ? requiredHits : 1;
                            }
                            pendingLong = (time, kind, hits, tag());
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
                            hitObjects.Add(new PlayableHitObject(time, value, isHand: symbol is 'A' or 'B') { Branch = tag() });
                        }
                    }
                    maxSeconds = Math.Max(maxSeconds, cursorSeconds);
                    cursorSeconds = checked(cursorSeconds + 60m / bpm * 4m * numerator / denominator / count);
                    ensureTimeFits(cursorSeconds, "The TJA chart exceeds the supported time range.");
                }
                measureData.Clear();
                commands.Clear();
            }
        }

        // The route being read ends; the main route's end state is what the chart goes on from.
        void endRoute()
        {
            if (route is null) return;
            // Commands after a route's last measure are that route's, not the next one's.
            foreach (var command in commands)
                applyCommand(command.Line, ref numerator, ref denominator, ref bpm, ref scroll,
                    ref isGoGo, ref isBarlineVisible, ref cursorSeconds, timing, scrolls, effects);
            commands.Clear();
            if (!onMain())
            {
                ownRoutes[route] = new TjaRouteStreams(timing, scrolls, effects, bars);
                return;
            }
            afterMain = save();
            mainTo = (hitObjects.Count, longNotes.Count);
        }

        void closeBranch()
        {
            if (!branch) return;
            endRoute();
            // A route the block lacks plays the main route's notes.
            if (routesSeen.Contains(mainRoute))
                foreach (var missing in (string[])["#N", "#E", "#M"])
                {
                    if (routesSeen.Contains(missing)) continue;
                    var copy = routeOf(missing);
                    for (var index = mainFrom.Notes; index < mainTo.Notes; index++)
                        hitObjects.Add(hitObjects[index] with { Branch = copy });
                    for (var index = mainFrom.Longs; index < mainTo.Longs; index++)
                        longNotes.Add(longNotes[index] with { Branch = copy });
                }
            if (ownRoutes.Count != 0)
                routeBlocks.Add(new TjaRouteBlock(chartTime(atBranch.Cursor), chartTime(afterMain.Cursor), ownRoutes));
            restore(afterMain);
            (timing, scrolls, effects, bars) = (timingPoints, scrollPoints, effectPoints, barLines);
            branch = false;
            route = null;
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
            longNotes.Add(new PlayableLongNote(note.Start, end, note.Kind, note.Hits) { Branch = note.Branch });
            pendingLong = null;
        }

        throw new InvalidDataException(active
            ? "The selected TJA chart has no #END marker."
            : "The selected TJA chart section no longer exists.");
    }

    private readonly record struct TjaState(int Numerator, int Denominator, decimal Bpm, double Scroll, bool GoGo,
        bool Barline, decimal Cursor, (TimeSpan Start, PlayableLongNoteKind Kind, int Hits, ChartBranch? Branch)? Pending);

    // The routes other than the main one, as written between Start and End.
    private sealed record TjaRouteBlock(TimeSpan Start, TimeSpan End, Dictionary<string, TjaRouteStreams> Routes);

    private sealed record TjaRouteStreams(ImmutableArray<ChartTimingPoint>.Builder Timing, ImmutableArray<ChartScrollPoint>.Builder Scroll,
        ImmutableArray<ChartEffectPoint>.Builder Effects, ImmutableArray<ChartBarLine>.Builder Bars);

    // A route's whole stream: the main route's points, but its own inside the blocks where it has some, back to
    // the main route's value when each ends. A scratch stream's seed comes before the block's Start.
    private static ImmutableArray<T> routeStream<T>(ImmutableArray<T>.Builder main, List<TjaRouteBlock> blocks, string route,
        Func<TjaRouteStreams, IEnumerable<T>> own,
        Func<T, TimeSpan> time, Func<T, TimeSpan, T> at)
    {
        var blocksOfRoute = blocks.Where(block => block.Routes.ContainsKey(route)).ToList();
        // Sorted stably: at one time the block's return comes first, then the main route's point, then a next block's.
        var returns = blocksOfRoute.SelectMany(block => main.Where(point => time(point) <= block.End).TakeLast(1).Select(point => at(point, block.End)));
        var outside = main.Where(point => !blocksOfRoute.Any(block => time(point) >= block.Start && time(point) < block.End));
        var inside = blocksOfRoute.SelectMany(block => own(block.Routes[route]).Where(point => time(point) >= block.Start));
        return [.. returns.Concat(outside).Concat(inside).OrderBy(time)];
    }

    // The route's Go-Go, only if it goes on and off differently from the main route's (repeated states don't count).
    private static ImmutableArray<ChartEffectPoint> ownEffects(ImmutableArray<ChartEffectPoint>.Builder main, ImmutableArray<ChartEffectPoint> route)
    {
        static IEnumerable<ChartEffectPoint> changes(IEnumerable<ChartEffectPoint> points)
        {
            var active = false;
            foreach (var point in points)
                if (point.IsGoGo != active)
                {
                    active = point.IsGoGo;
                    yield return point;
                }
        }
        return changes(route).SequenceEqual(changes(main)) ? [] : route;
    }

    // The route's bar lines, only if they differ from the main route's: its own in its blocks, the main ones elsewhere.
    private static ImmutableArray<ChartBarLine> ownBars(ImmutableArray<ChartBarLine>.Builder main, List<TjaRouteBlock> blocks, string route)
    {
        var blocksOfRoute = blocks.Where(block => block.Routes.ContainsKey(route)).ToList();
        ImmutableArray<ChartBarLine> own = [.. main.Where(bar => !blocksOfRoute.Any(block => bar.Time >= block.Start && bar.Time < block.End))
            .Concat(blocksOfRoute.SelectMany(block => block.Routes[route].Bars)).OrderBy(static bar => bar.Time)];
        return own.SequenceEqual(main.OrderBy(static bar => bar.Time)) ? [] : own;
    }

    // A #BRANCHSTART: judged At, its routes from Start; Condition "p,80,90" etc.; routes under #LEVELHOLD.
    private sealed record TjaCheck(TimeSpan At, TimeSpan Start, string Condition, ChartBranch[] Held);

    private static ChartBranch routeOf(string marker) => marker switch
    {
        "#N" => ChartBranch.Normal,
        "#E" => ChartBranch.Expert,
        _ => ChartBranch.Master,
    };

    /// <summary>
    /// The checks as fumen thresholds, the way tja2fumen converts them: <c>r,a,b</c> are roll hits;
    /// <c>p,a,b</c> percentages of the most points the route's notes and balloons since the previous check
    /// can earn (0 or less: always, over 100: never). Only rolls count when every condition is a roll
    /// count (or a p that is always/never met), only notes and balloons when none is.
    /// </summary>
    /// <remarks>
    /// ponytail: points reset at every check as in fumen, so a TJA counting on past one without #SECTION
    /// counts from the last check; <c>s</c> (score) conditions keep the route.
    /// </remarks>
    private static ChartBranching branching(List<TjaCheck> checks, ImmutableArray<PlayableHitObject> notes,
        ImmutableArray<PlayableLongNote> longNotes)
    {
        var parsed = checks.Select(static check =>
        {
            var parts = check.Condition.Split(',', StringSplitOptions.TrimEntries);
            var type = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            double value(int index) => parts.Length > index
                && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) ? number : 0;
            return (Check: check, Type: type, Expert: value(1), Master: value(2));
        }).ToArray();
        var always = (double percent) => percent <= 0 || percent > 100;
        var rollsOnly = parsed.All(item => item.Type == "r" || item.Type == "p" && always(item.Expert) && always(item.Master));
        var notesOnly = parsed.All(static item => item.Type != "r");
        var points = ChartBranchPoints.Default;
        if (rollsOnly)
            points = points with { Great = 0, Good = 0, BigGreat = 0, BigGood = 0, Balloon = 0, Kusudama = 0 };
        else if (notesOnly)
            points = points with { Roll = 0, BigRoll = 0 };
        var result = ImmutableArray.CreateBuilder<ChartBranchCheck>(parsed.Length);
        var since = TimeSpan.Zero;
        foreach (var (check, type, expert, master) in parsed)
        {
            ChartBranchThreshold row(ChartBranch route)
            {
                if (check.Held.Contains(route))
                    return route switch
                    {
                        ChartBranch.Normal => new(int.MaxValue, int.MaxValue),
                        ChartBranch.Expert => new(0, int.MaxValue),
                        _ => new(0, 0),
                    };
                if (type == "r")
                    return new(rolls(expert), rolls(master));
                if (type != "p")
                    return ChartBranchThreshold.Stay;
                var most = notes.Where(note => note.StartTime >= since && note.StartTime < check.At && PlayableChart.IsOn(note.Branch, route))
                        .Sum(note => (long)(note.IsStrong ? points.BigGreat : points.Great))
                    + longNotes.Where(note => note.IsBalloon && note.StartTime >= since && note.StartTime < check.At
                            && PlayableChart.IsOn(note.Branch, route))
                        .Sum(note => (long)(note.Kind == PlayableLongNoteKind.Balloon ? points.Balloon : points.Kusudama));
                int percentOf(double percent) => percent <= 0 ? 0 : percent > 100 ? int.MaxValue
                    : (int)Math.Min(int.MaxValue, Math.Floor(most * percent / 100));
                return new(percentOf(expert), percentOf(master));
            }
            result.Add(new ChartBranchCheck(check.At, check.Start, row(ChartBranch.Normal), row(ChartBranch.Expert), row(ChartBranch.Master)));
            since = check.At;
        }
        return new ChartBranching(result.MoveToImmutable(), points);

        static int rolls(double hits) => hits <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(hits));
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
                appendOrReplace(timingPoints, new ChartTimingPoint(controlTime(timingPoints, cursorSeconds), (double)bpm, numerator, denominator));
                break;
            case "#BPMCHANGE":
                bpm = positiveNumber(argument, "#BPMCHANGE");
                appendOrReplace(timingPoints, new ChartTimingPoint(controlTime(timingPoints, cursorSeconds), (double)bpm, numerator, denominator));
                break;
            case "#SCROLL":
                scroll = finiteDouble(argument, "#SCROLL");
                appendOrReplace(scrollPoints, new ChartScrollPoint(controlTime(scrollPoints, cursorSeconds), scroll));
                break;
            case "#DELAY":
                // Negative delays are legal: editors offset one note with #DELAY x … #DELAY -x.
                cursorSeconds = checked(cursorSeconds + finiteDecimal(argument, "#DELAY"));
                ensureTimeFits(cursorSeconds, "TJA #DELAY places the chart outside the supported time range.");
                break;
            case "#GOGOSTART":
                isGoGo = true;
                appendOrReplace(effectPoints, new ChartEffectPoint(controlTime(effectPoints, cursorSeconds), true));
                break;
            case "#GOGOEND":
                isGoGo = false;
                appendOrReplace(effectPoints, new ChartEffectPoint(controlTime(effectPoints, cursorSeconds), false));
                break;
            case "#BARLINEON":
                isBarlineVisible = true;
                break;
            case "#BARLINEOFF":
                isBarlineVisible = false;
                break;
            case "#SECTION":
            case "#SENOTECHANGE":
            case "#LYRIC":
                // Points reset at every check anyway (branching); lyric/sound-decoration commands do not alter timing.
                break;
            default:
                throw new NotSupportedException($"TJA command '{name}' is not supported by the gameplay loader.");
        }
    }

    // Control points stay in file order: one a negative #DELAY moves before the previous point takes
    // effect at that point's time, so the last command written still wins for the notes after it.
    private static TimeSpan controlTime<T>(ImmutableArray<T>.Builder points, decimal seconds)
        where T : struct
    {
        var time = chartTime(seconds);
        var previousTime = timeOf(points[^1]);
        return time > previousTime ? time : previousTime;
    }

    private static void appendOrReplace<T>(ImmutableArray<T>.Builder points, T point)
        where T : struct
    {
        if (timeOf(points[^1]) == timeOf(point))
            points[^1] = point;
        else
            points.Add(point);
    }

    private static TimeSpan timeOf<T>(T point) where T : struct => point switch
    {
        ChartTimingPoint value => value.Time,
        ChartScrollPoint value => value.Time,
        ChartEffectPoint value => value.Time,
        _ => throw new InvalidOperationException("Unsupported control point type."),
    };

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
