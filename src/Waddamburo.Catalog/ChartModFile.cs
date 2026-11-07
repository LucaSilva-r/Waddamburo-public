using System.Collections.Immutable;
using System.Text.Json;

namespace Waddamburo.Catalog;

/// <summary>A timed transition compiled from keyframes (beats measured from chart start).</summary>
public readonly record struct ChartModCue(double Beat, double Beats, ChartEase Ease, string Target, string Property,
    double Value, ChartModCurve? Curve = null, bool Initial = false);

/// <summary>A value at a beat. Ease or Curve describes the segment into this key.</summary>
public readonly record struct ChartModKeyframe(double Beat, double Value, ChartEase Ease = ChartEase.Linear,
    ChartModCurve? Curve = null);

/// <summary>Version 1 keyframe tracks, either at the root or in per-course objects under "courses".</summary>
public static class ChartModFile
{
    public const string Extension = ".wddmod";
    public const int Version = 1;
    public const int MaxBytes = 4 << 20;
    private const int MaxKeys = 100_000;

    /// <summary>Loads and compiles tracks into transitions for existing gameplay consumers.</summary>
    public static IReadOnlyList<ChartModCue> Read(ReadOnlySpan<byte> json, TaikoCourse? course) =>
        Compile(ReadTracks(json, course));

    /// <summary>Root, wildcard, then course tracks; a more specific track replaces that whole property.</summary>
    public static ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>> ReadTracks(ReadOnlySpan<byte> json, TaikoCourse? course)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("The modchart file is too large.");
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("wddmod", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var fileVersion) || fileVersion != Version)
                throw new InvalidDataException($"Not a version {Version} modchart file.");
            var tracks = ImmutableDictionary.CreateBuilder<string, ImmutableArray<ChartModKeyframe>>(StringComparer.Ordinal);
            var count = 0;
            readSection(root);
            if (root.TryGetProperty("courses", out var courses))
            {
                if (courses.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected course objects.");
                foreach (var key in course is { } own ? (string[])["*", CourseKey(own)] : ["*"])
                    if (courses.TryGetProperty(key, out var section)) readSection(section);
            }
            return tracks.ToImmutable();

            void readSection(JsonElement section)
            {
                if (section.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a modchart section.");
                if (section.TryGetProperty("changes", out _))
                    throw new InvalidDataException("This modchart uses the old changes format; migrate it to keyframe tracks.");
                if (!section.TryGetProperty("tracks", out var entries)) return;
                if (entries.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected tracks keyed by target.property.");
                foreach (var entry in entries.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected a keyframe array.");
                    count += entry.Value.GetArrayLength();
                    if (count > MaxKeys) throw new InvalidDataException("The modchart has too many keyframes.");
                    var key = entry.Name.ToLowerInvariant();
                    if (!Known(key)) continue;
                    var keys = new List<ChartModKeyframe>();
                    foreach (var item in entry.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a keyframe object.");
                        var beat = number(item, "beat");
                        var value = number(item, "value");
                        var ease = ChartEase.Linear;
                        ChartModCurve? curve = null;
                        if (item.TryGetProperty("curve", out var handles))
                        {
                            if (handles.ValueKind != JsonValueKind.Array || handles.GetArrayLength() != 4)
                                throw new InvalidDataException("A Bézier curve needs four handles.");
                            curve = new ChartModCurve(finite(handles[0]), finite(handles[1]), finite(handles[2]), finite(handles[3]));
                            if (!curve.Value.IsValid) throw new InvalidDataException("Bézier X handles must be in [0,1].");
                            if (item.TryGetProperty("ease", out _)) throw new InvalidDataException("Use ease or curve, not both.");
                        }
                        else if (item.TryGetProperty("ease", out var easing)
                            && (easing.ValueKind != JsonValueKind.String || !ChartMods.TryParseEase(easing.GetString()!, out ease)))
                            continue; // Unknown easings from newer authors are ignored.
                        keys.Add(new(beat, value, ease, curve));
                    }
                    var ordered = keys.OrderBy(k => k.Beat).ToImmutableArray();
                    validate(key, ordered);
                    tracks[key] = ordered;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The modchart file is not valid JSON.", exception);
        }
    }

    /// <summary>Writes deterministic UTF-8 JSON without serializer reflection.</summary>
    public static byte[] Write(IReadOnlyDictionary<string, ImmutableArray<ChartModKeyframe>> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Values.Sum(keys => keys.IsDefault ? throw new InvalidDataException("Invalid keyframe array.") : (long)keys.Length) > MaxKeys) throw new InvalidDataException("Too many keyframes.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("wddmod", Version);
            writer.WriteStartObject("tracks");
            foreach (var (key, keys) in tracks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                validate(key, keys);
                writer.WriteStartArray(key);
                foreach (var k in keys)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("beat", k.Beat);
                    writer.WriteNumber("value", k.Value);
                    if (k.Curve is { } c)
                    {
                        writer.WriteStartArray("curve");
                        writer.WriteNumberValue(c.X1); writer.WriteNumberValue(c.Y1);
                        writer.WriteNumberValue(c.X2); writer.WriteNumberValue(c.Y2);
                        writer.WriteEndArray();
                    }
                    else if (k.Ease != ChartEase.Linear)
                        writer.WriteString("ease", JsonNamingPolicy.CamelCase.ConvertName(k.Ease.ToString()));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        if (stream.Length > MaxBytes) throw new InvalidDataException("The modchart file is too large.");
        return stream.ToArray();
    }

    /// <summary>The first value holds before its beat; later keys ease from their predecessor.</summary>
    public static IReadOnlyList<ChartModCue> Compile(IReadOnlyDictionary<string, ImmutableArray<ChartModKeyframe>> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Values.Sum(keys => keys.IsDefault ? throw new InvalidDataException("Invalid keyframe array.") : (long)keys.Length) > MaxKeys) throw new InvalidDataException("Too many keyframes.");
        var cues = new List<ChartModCue>();
        foreach (var (key, keys) in tracks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            validate(key, keys);
            var dot = key.IndexOf('.');
            for (var i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                var start = i == 0 ? k.Beat : keys[i - 1].Beat;
                cues.Add(new(start, i == 0 ? 0 : k.Beat - start, k.Ease, key[..dot], key[(dot + 1)..], k.Value, k.Curve, Initial: i == 0));
            }
        }
        return cues.OrderBy(c => c.Beat).ToArray();
    }

    public static PlayableChart Apply(PlayableChart chart, IReadOnlyDictionary<string, ImmutableArray<ChartModKeyframe>> tracks)
    {
        var applied = Apply(chart, Compile(tracks));
        return applied with { Effects = applied.Effects with { ModTracks = tracks.ToImmutableDictionary(StringComparer.Ordinal) } };
    }

    /// <summary>Replaces drawing mods while preserving the chart's other effects and identity.</summary>
    public static PlayableChart Apply(PlayableChart chart, IReadOnlyList<ChartModCue> cues)
    {
        var beats = chart.Effects.Beats.IsEmpty ? new ChartEffects { Beats = BeatsOf(chart.TimingPoints) } : chart.Effects;
        TimeSpan at(double beat) => beats.TimeAtBeat(beat) is var time && time < TimeSpan.Zero ? TimeSpan.Zero : time;
        var mods = cues.Select(cue => (Start: cue.Initial ? TimeSpan.Zero : at(cue.Beat), End: cue.Initial ? TimeSpan.Zero : at(cue.Beat + cue.Beats), Cue: cue))
            .Select(static t => new ChartModEase(t.Start, t.End - t.Start, t.Cue.Target, t.Cue.Property, t.Cue.Value, t.Cue.Ease, t.Cue.Curve, t.Cue.Initial))
            .OrderBy(static change => change.Start);
        var tracks = cues.GroupBy(c => c.Target + "." + c.Property)
            .Where(group => group.Any(c => c.Initial))
            .ToImmutableDictionary(group => group.Key, group => group.Select(c =>
                new ChartModKeyframe(c.Initial ? c.Beat : c.Beat + c.Beats, c.Value, c.Ease, c.Curve)).ToImmutableArray());
        return chart with { Effects = chart.Effects with { Mods = [.. mods], ModTracks = tracks } };
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

    public static bool Known(string key)
    {
        var dot = key.IndexOf('.');
        return dot > 0 && ChartMods.IsKnown(key[..dot], key[(dot + 1)..]);
    }

    private static void validate(string key, ImmutableArray<ChartModKeyframe> keys)
    {
        if (!Known(key) || keys.IsDefault) throw new InvalidDataException("Unknown track or invalid keyframe array.");
        for (var i = 0; i < keys.Length; i++)
            if (!double.IsFinite(keys[i].Beat) || !double.IsFinite(keys[i].Value) || !Enum.IsDefined(keys[i].Ease)
                || keys[i].Curve is { IsValid: false } || i > 0 && keys[i].Beat <= keys[i - 1].Beat)
                throw new InvalidDataException("Keyframes need finite values, valid curves and strictly increasing beats.");
    }

    private static double number(JsonElement entry, string name) => entry.TryGetProperty(name, out var value)
        ? finite(value) : throw new InvalidDataException($"Missing keyframe {name}.");

    private static double finite(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var result) && double.IsFinite(result) ? result
        : throw new InvalidDataException("Expected a finite number.");
}
