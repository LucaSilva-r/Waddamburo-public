using System.Text.Json;

namespace Waddamburo.Catalog;

/// <summary>A modchart change as a .wddmod file writes it: in beats from the chart's start (before tempo is applied).</summary>
public readonly record struct ChartModCue(double Beat, double Beats, ChartEase Ease, string Target, string Property, double Value);

/// <summary>
/// A chart's modchart file (<c>Song.wddmod</c> beside <c>Song.tja</c>): JSON, so the chart stays as its author wrote it and
/// effects can be added in Waddamburo whatever editor made the notes.
/// <code>
/// { "wddmod": 1,
///   "courses": {
///     "*":   [ { "beat": 0, "beats": 8, "ease": "inOutSine", "target": "don.yaw", "value": 360 } ],
///     "oni": [ { "beat": 16, "target": "notes.drunk", "value": 1 } ] } }
/// </code>
/// "*" applies to every course; a player's own file for one chart (named by its <see cref="ChartHash"/>) just lists
/// <c>"changes": [ … ]</c>. "beats" (default 0: at once) and "ease" (default linear) may be left out. Beats count
/// from #START along the chart's tempo, and a #DELAY stops them. Targets and properties are <see cref="ChartMods"/>'s.
/// </summary>
public static class ChartModFile
{
    public const string Extension = ".wddmod";
    public const int Version = 1;
    public const int MaxBytes = 4 << 20;
    private const int MaxCues = 100_000;

    /// <summary>
    /// The cues for <paramref name="course"/> ("*" first, then the course's own; null: "*" alone). An unknown target,
    /// property or easing skips its entry (a newer file still plays); a broken or newer-version file throws
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<ChartModCue> Read(ReadOnlySpan<byte> json, TaikoCourse? course)
    {
        if (json.Length > MaxBytes)
            throw new InvalidDataException("The modchart file is too large.");
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!root.TryGetProperty("wddmod", out var version) || !version.TryGetInt32(out var number) || number is < 1 or > Version)
                throw new InvalidDataException($"Not a version {Version} modchart file.");
            var cues = new List<ChartModCue>();
            var lists = new List<JsonElement>();
            if (root.TryGetProperty("changes", out var changes))
                lists.Add(changes);
            if (root.TryGetProperty("courses", out var courses) && courses.ValueKind == JsonValueKind.Object)
                foreach (var key in course is { } own ? (string[])["*", CourseKey(own)] : ["*"])
                    if (courses.TryGetProperty(key, out var entries))
                        lists.Add(entries);
            foreach (var entries in lists)
            {
                if (entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (cues.Count == MaxCues)
                        throw new InvalidDataException("The modchart file has too many entries.");
                    if (cue(entry) is { } read)
                        cues.Add(read);
                }
            }
            return cues;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The modchart file is not valid JSON: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// <paramref name="chart"/> with these cues as its modchart (in place of its own), timed along its beat count:
    /// <see cref="ChartEffects.Beats"/> when it has one (a TJA with #DELAY or beat scrolling), else its tempo points.
    /// </summary>
    public static PlayableChart Apply(PlayableChart chart, IReadOnlyList<ChartModCue> cues)
    {
        var beats = chart.Effects.Beats.IsEmpty ? new ChartEffects { Beats = BeatsOf(chart.TimingPoints) } : chart.Effects;
        TimeSpan at(double beat) => beats.TimeAtBeat(beat) is var time && time < TimeSpan.Zero ? TimeSpan.Zero : time;
        var mods = cues.Select(cue => (Start: at(cue.Beat), End: at(cue.Beat + cue.Beats), Cue: cue))
            .Select(static timed => new ChartModEase(timed.Start, timed.End - timed.Start, timed.Cue.Target, timed.Cue.Property, timed.Cue.Value, timed.Cue.Ease))
            .OrderBy(static change => change.Start);
        return chart with { Effects = chart.Effects with { Mods = [.. mods] } };
    }

    /// <summary>The beat count over time from tempo points alone (no #DELAY to stop it).</summary>
    public static System.Collections.Immutable.ImmutableArray<ChartBeatPoint> BeatsOf(IEnumerable<ChartTimingPoint> timing)
    {
        var points = new List<ChartBeatPoint>();
        foreach (var point in timing)
        {
            var beat = points.Count == 0 ? 0 : points[^1].Beat + (point.Time - points[^1].Time).TotalSeconds * points[^1].BeatsPerSecond;
            points.Add(new ChartBeatPoint(point.Time, beat, point.BeatsPerMinute / 60));
        }
        return [.. points];
    }

    /// <summary>The file's name for a course (Edit is ura).</summary>
    public static string CourseKey(TaikoCourse course) => course switch
    {
        TaikoCourse.Easy => "easy",
        TaikoCourse.Normal => "normal",
        TaikoCourse.Hard => "hard",
        TaikoCourse.Oni => "oni",
        _ => "ura",
    };

    private static ChartModCue? cue(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !number(entry, "beat", out var beat) || !number(entry, "value", out var value)
            || !entry.TryGetProperty("target", out var name) || name.GetString() is not { } targetProperty)
            return null;
        var dot = targetProperty.IndexOf('.');
        var (target, property) = dot < 0 ? ("", "") : (targetProperty[..dot].ToLowerInvariant(), targetProperty[(dot + 1)..].ToLowerInvariant());
        var beats = number(entry, "beats", out var length) && length > 0 ? length : 0;
        var ease = ChartEase.Linear;
        if (!ChartMods.IsKnown(target, property)
            || entry.TryGetProperty("ease", out var easing) && !(easing.GetString() is { } easeName && ChartMods.TryParseEase(easeName, out ease)))
            return null;
        return new ChartModCue(beat, beats, ease, target, property, value);
    }

    private static bool number(JsonElement entry, string name, out double value)
    {
        value = 0;
        return entry.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }
}
