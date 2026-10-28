using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
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
        // ponytail: the timeline judges the play a second time; one pass if rescoring gets slow.
        writer.WriteStartObject("timeline");
        PlayTimeline.Write(charts, play, writer);
        writer.WriteEndObject();
    });

    /// <summary>One stored play as TaikOnline sends it.</summary>
    public readonly record struct StoredPlay(byte[] Notes, string? Source, int Options, int? Seed, byte[] Replay);

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
                write(charts, new StoredPlay(notes, source, options, seed, replay), writer);
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
            TaikoGameplayPresentation.WindowsFor(stored.Chart, stored.Course), TaikoGameplayPresentation.StrongSecondHitWindow);
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
