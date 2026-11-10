using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Cli;

/// <summary>
/// --rescore: TaikOnline's scorer. Reads JSON lines on stdin, one stored play each: <c>id</c>, <c>notes</c>
/// (the chart import's gzipped canonical notes, base64), <c>source</c> (the chart's <see cref="SongSourceKind"/>
/// name, for its windows), <c>options</c>, <c>seed</c>, <c>replay</c> (base64). Writes one JSON line per play:
/// its id and the result under <see cref="PlayRecord.CurrentScoringVersion"/> with its <see cref="PlayTimeline"/>,
/// or its id and an error.
/// No game data, window or audio.
/// </summary>
internal static class Rescore
{
    public static int Run(Stream input, Stream output) => Each(input, output, static (charts, play, writer) =>
    {
        var result = Score(charts, play.Notes, play.Source, play.Options, play.Seed, play.Replay);
        writer.WriteNumber("course", (int)result.Course);
        writer.WriteNumber("score", result.Score);
        writer.WriteNumber("great", result.Great);
        writer.WriteNumber("good", result.Good);
        writer.WriteNumber("miss", result.Miss);
        writer.WriteNumber("max_combo", result.MaxCombo);
        writer.WriteNumber("rolls", result.Rolls);
        writer.WriteNumber("roll_max", result.RollMax); // the chart's, beside the play's drumroll hits
        writer.WriteNumber("gauge", result.GaugeSegments);
        writer.WriteBoolean("cleared", result.Cleared);
        writer.WriteNumber("scoring_version", PlayRecord.CurrentScoringVersion);
        if (play.AiBattle is { } battle)
        {
            try { writeBattle(charts, play, battle, writer); }
            catch (Exception exception) when (IsBadPlay(exception) || exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                writer.WriteString("ai_error", exception.Message);
            }
        }
        // ponytail: the timeline judges the play a second time; one pass if rescoring gets slow.
        writer.WriteStartObject("timeline");
        PlayTimeline.Write(charts, play, writer);
        writer.WriteEndObject();
    });

    /// <summary>
    /// --rescore-dan: dan courses, one JSON line each: <c>id</c>, <c>requirements</c> (type, red, gold), <c>songs</c>
    /// (<c>notes</c>, <c>source</c>, <c>note_count</c>, <c>replay</c>, base64; no replay: not reached). Writes its id with
    /// the total, each song's counts, <c>songs_played</c>, <c>result_level</c>, <c>progress</c> and each song reached's
    /// <c>timelines</c> entry, or its id and an error.
    /// </summary>
    public static int RunCourses(Stream input, Stream output)
    {
        using var reader = new StreamReader(input);
        var charts = new ChartCache();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            string? id = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var play = document.RootElement;
                id = play.GetProperty("id").GetString();
                writer.WriteString("id", id);
                writeCourse(charts, play, writer);
            }
            catch (Exception exception) when (IsBadPlay(exception) || exception is JsonException or KeyNotFoundException
                or InvalidOperationException)
            {
                if (id is null)
                    writer.WriteNull("id");
                writer.WriteString("error", exception.Message);
            }
            writer.WriteEndObject();
            writer.Flush();
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        return 0;
    }

    private static void writeCourse(ChartCache charts, JsonElement play, Utf8JsonWriter writer)
    {
        var songs = play.GetProperty("songs").EnumerateArray().ToList();
        var stored = songs.Select(song => Chart(charts, Convert.FromBase64String(song.GetProperty("notes").GetString() ?? ""),
            song.TryGetProperty("source", out var source) ? source.GetString() : null)).ToList();
        static int[] values(JsonElement requirement, string name) => [.. requirement.GetProperty(name).EnumerateArray().Select(static value => value.GetInt32())];
        var course = new DanCourse(0, "", "", [.. play.GetProperty("requirements").EnumerateArray().Select(static requirement =>
                new DanRequirement(requirement.GetProperty("type").GetString() ?? "", values(requirement, "red"), values(requirement, "gold")))],
            [.. songs.Select(static song => new DanCourseSong("", 0, null, song.GetProperty("note_count").GetInt32(), []))],
            play.TryGetProperty("rules", out var rules) ? rules.GetString() : null);
        var windows = TaikoGameplayPresentation.WindowsFor(stored[0].Chart, stored[0].Course);
        var replays = songs.Select(static song => song.TryGetProperty("replay", out var replay) ? Convert.FromBase64String(replay.GetString() ?? "") : []).ToList();
        var progress = new List<(int Song, TimeSpan Time, DanProgress At)>();
        var (total, counts, played, level) = PlayRescore.ScoreCourse(course, DanCourseTimeline.Merge(stored),
            [.. replays.Select(static replay => replay.Length > 0 ? TaikoReplay.Decode(replay) : new TaikoReplay())],
            windows, (song, time, at) => progress.Add((song, time, at)));
        writer.WriteNumber("score", total.Score);
        writer.WriteNumber("great", total.Goods);
        writer.WriteNumber("good", total.Oks);
        writer.WriteNumber("miss", total.Bads);
        writer.WriteNumber("max_combo", total.MaxCombo);
        writer.WriteNumber("rolls", total.Rolls);
        writer.WriteNumber("gauge_percent", total.GaugePercent);
        writer.WriteNumber("songs_played", played);
        writer.WriteNumber("result_level", level);
        writer.WriteNumber("scoring_version", PlayRecord.CurrentScoringVersion);
        writer.WriteStartArray("songs");
        foreach (var song in counts)
        {
            writer.WriteStartObject();
            writer.WriteNumber("great", song.Goods);
            writer.WriteNumber("good", song.Oks);
            writer.WriteNumber("miss", song.Bads);
            writer.WriteNumber("rolls", song.Rolls);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        // The numbers as the course goes, for the run page's requirements: song, ms, gauge %, goods, oks, bads, combo, rolls, score.
        writer.WriteStartArray("progress");
        foreach (var (song, time, at) in progress)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(song);
            writer.WriteNumberValue((long)time.TotalMilliseconds);
            writer.WriteNumberValue(at.GaugePercent);
            writer.WriteNumberValue(at.Goods);
            writer.WriteNumberValue(at.Oks);
            writer.WriteNumberValue(at.Bads);
            writer.WriteNumberValue(at.MaxCombo);
            writer.WriteNumberValue(at.Rolls);
            writer.WriteNumberValue(at.Score);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        // Each song reached as a normal replay of its chart.
        writer.WriteStartArray("timelines");
        for (var song = 0; song < played; song++)
        {
            writer.WriteStartObject();
            PlayTimeline.Write(charts, new StoredPlay(Convert.FromBase64String(songs[song].GetProperty("notes").GetString() ?? ""),
                songs[song].TryGetProperty("source", out var source) ? source.GetString() : null, 0, null, replays[song].Length > 0 ? replays[song] : new TaikoReplay().Encode()),
                writer, windows);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>One stored play as TaikOnline sends it.</summary>
    public readonly record struct StoredPlay(byte[] Notes, string? Source, int Options, int? Seed, byte[] Replay)
    {
        public JsonElement? AiBattle { get; init; }
    }

    private static void writeBattle(ChartCache charts, StoredPlay play, JsonElement setup, Utf8JsonWriter writer)
    {
        if (setup.GetProperty("version").GetInt32() != AiBattleSimulation.Version || play.Seed is null)
            throw new InvalidDataException("Unsupported AI battle version or missing seed.");
        var (chart, course) = Chart(charts, play.Notes, play.Source);
        var support = setup.GetProperty("support_level").GetInt32();
        if (support != 0)
        {
            if (support < 1 || support >= ChartSimplifier.LevelOf(course) || ChartSimplifier.SourceCourse(support) != course)
                throw new InvalidDataException("Invalid AI support level for this course.");
            var original = Chart(charts, Convert.FromBase64String(setup.GetProperty("base_notes").GetString() ?? ""),
                setup.GetProperty("base_source").GetString());
            if (original.Course != course || ChartHash.Compute(ChartSimplifier.Simplify(original.Chart, support), course) != ChartHash.Compute(chart, course))
                throw new InvalidDataException("The intermediate chart does not match its approved base chart.");
        }
        double? value(string name) => setup.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : null;
        (double, double)? prior = value("hit_median_ms") is { } median && value("hit_variance") is { } variance ? (median, variance) : null;
        var simulation = new AiBattleSimulation(TaikoPlayOptions.FromBits(play.Options).Apply(chart, play.Seed.Value), course, play.Seed.Value, prior);
        simulation.Advance(TaikoReplay.Decode(play.Replay).Inputs, simulation.End + TimeSpan.FromTicks(1));
        var outcome = simulation.Outcome;
        writer.WriteStartObject("ai_battle");
        writer.WriteNumber("version", AiBattleSimulation.Version);
        writer.WriteString("sections", string.Concat(outcome.Sections.Select(static section => section.Won ? 'W' : 'L')));
        writer.WriteNumber("level", chart.Level ?? 0);
        if (simulation.Timing is { } timing)
        {
            writer.WriteNumber("hit_median_ms", timing.Median);
            writer.WriteNumber("hit_variance", timing.Variance);
        }
        writer.WriteStartArray("counts");
        foreach (var section in outcome.Sections)
        {
            writer.WriteStartObject();
            counts("player", section.Player);
            counts("ai", section.Ai);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        void counts(string name, Waddamburo.Game.Lumen.AiSectionCounts counts)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("great", counts.Great);
            writer.WriteNumber("good", counts.Good);
            writer.WriteNumber("miss", counts.Miss);
            writer.WriteNumber("rolls", counts.Rolls);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// The JSON-lines loop: each line's play handed to <paramref name="write"/>
    /// inside its <c>{"id": ...}</c> object, or its id and an error when it cannot be read or played.
    /// </summary>
    private static int Each(Stream input, Stream output, Action<ChartCache, StoredPlay, Utf8JsonWriter> write)
    {
        using var reader = new StreamReader(input);
        var charts = new ChartCache();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string? id = null;
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            try
            {
                using var document = JsonDocument.Parse(line);
                var play = document.RootElement;
                id = play.GetProperty("id").GetString();
                writer.WriteString("id", id);
                var source = play.TryGetProperty("source", out var named) ? named.GetString() : null;
                var notes = Convert.FromBase64String(play.GetProperty("notes").GetString() ?? "");
                var options = play.TryGetProperty("options", out var bits) && bits.ValueKind == JsonValueKind.Number ? bits.GetInt32() : 0;
                int? seed = play.TryGetProperty("seed", out var drawn) && drawn.ValueKind == JsonValueKind.Number ? drawn.GetInt32() : null;
                var replay = Convert.FromBase64String(play.GetProperty("replay").GetString() ?? "");
                write(charts, new StoredPlay(notes, source, options, seed, replay)
                {
                    AiBattle = play.TryGetProperty("ai_battle", out var battle) && battle.ValueKind == JsonValueKind.Object ? battle : null,
                }, writer);
            }
            catch (Exception exception) when (IsBadPlay(exception) || exception is JsonException or KeyNotFoundException
                or InvalidOperationException)
            {
                if (id is null)
                    writer.WriteNull("id");
                writer.WriteString("error", exception.Message);
            }
            writer.WriteEndObject();
            writer.Flush();
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        return 0;
    }

    /// <summary>Charts already read, by their stored bytes and source (a batch has many plays per chart).</summary>
    public sealed class ChartCache : Dictionary<(string Notes, SongSourceKind Source), (PlayableChart Chart, TaikoCourse Course)>;

    /// <summary>
    /// A stored play (its chart's gzipped canonical notes and <see cref="SongSourceKind"/> name, options bits,
    /// seed, replay) scored under the current rules, as a live lane scores it. Bad data throws; see <see cref="IsBadPlay"/>.
    /// </summary>
    public static TaikoPlayResult Score(ChartCache charts, byte[] notes, string? source, int options, int? seed, byte[] replay)
    {
        var stored = Chart(charts, notes, source);
        return PlayRescore.Score(stored.Chart, stored.Course, TaikoPlayOptions.FromBits(options), seed, TaikoReplay.Decode(replay),
            TaikoGameplayPresentation.WindowsFor(stored.Chart, stored.Course));
    }

    /// <summary>A stored chart (gzipped canonical notes and its <see cref="SongSourceKind"/> name), read once per batch.</summary>
    public static (PlayableChart Chart, TaikoCourse Course) Chart(ChartCache charts, byte[] notes, string? source)
    {
        var kind = Enum.TryParse<SongSourceKind>(source, out var named) ? named : SongSourceKind.Tja;
        var key = (Convert.ToBase64String(SHA256.HashData(notes)), kind);
        if (!charts.TryGetValue(key, out var stored))
            charts[key] = stored = ChartHash.Deserialize(gunzip(notes), kind);
        return stored;
    }

    /// <summary>What <see cref="Score"/> throws for a play it cannot score (corrupt chart or replay).</summary>
    public static bool IsBadPlay(Exception exception) => exception is FormatException or InvalidDataException
        or EndOfStreamException or ArgumentException or OverflowException or IOException;

    private static byte[] gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var result = new MemoryStream();
        input.CopyTo(result);
        return result.ToArray();
    }
}
