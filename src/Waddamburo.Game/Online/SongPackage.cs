using System.Buffers.Binary;
using System.Text.Json;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Online;

/// <summary>One course of a sent song: its chart as the host's game reads it, and how Song Select shows it.</summary>
public sealed record PackagedChart(string StableId, TaikoCourse Course, int? Level, string DifficultyName, PlayableChart Chart);

/// <summary>
/// A custom song the host sends a guest who lacks it: what Song Select shows, every course's chart in its canonical
/// playable form (<see cref="ChartHash"/>), and the audio as the host's file has it. Never a source file: the guest only
/// reads these bytes and the audio, through the same decoder the host's game played it with.
/// </summary>
/// <remarks>
/// Bytes: "WDBS", version, header length (u32), UTF-8 JSON header, then per chart a length (u32) and its bytes, then the
/// audio's length (u32) and bytes.
/// </remarks>
public sealed record SongPackage(SongKey Song, SongTitle Title, string? Artist, string? Subtitle, TimeSpan? PreviewStart,
    IReadOnlyList<PackagedChart> Charts, byte[] Audio)
{
    private const byte Version = 1;
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxText = 256;
    private static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(30);

    public byte[] Encode()
    {
        using var header = new MemoryStream();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("source", Song.Source.ToString());
            writer.WriteString("song", Song.StableId);
            writer.WriteString("title", Title.Primary);
            writer.WriteString("title_ja", Title.Japanese);
            writer.WriteString("title_en", Title.English);
            writer.WriteString("artist", Artist);
            writer.WriteString("subtitle", Subtitle);
            if (PreviewStart is { } preview)
                writer.WriteNumber("preview_ms", (long)preview.TotalMilliseconds);
            writer.WriteStartArray("charts");
            foreach (var chart in Charts)
            {
                writer.WriteStartObject();
                writer.WriteString("id", chart.StableId);
                writer.WriteNumber("course", (int)chart.Course);
                if (chart.Level is { } level)
                    writer.WriteNumber("level", level);
                writer.WriteString("name", chart.DifficultyName);
                writer.WriteNumber("duration_ms", (long)chart.Chart.Duration.TotalMilliseconds);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var output = new MemoryStream();
        output.Write("WDBS"u8);
        output.WriteByte(Version);
        writeBlock(output, header.ToArray());
        foreach (var chart in Charts)
            writeBlock(output, ChartHash.Serialize(chart.Chart, chart.Course));
        writeBlock(output, Audio);
        return output.ToArray();
    }

    /// <summary>
    /// Reads a package from the other player, null when anything in it doesn't add up or it isn't the song
    /// <paramref name="expected"/> (the one they picked).
    /// </summary>
    public static SongPackage? Decode(byte[] data, SongKey expected)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            var span = data.AsSpan();
            if (span.Length < 5 || !span[..4].SequenceEqual("WDBS"u8) || span[4] != Version)
                return null;
            var position = 5;
            var headerBytes = readBlock(span, ref position, MaxHeaderBytes);
            using var document = JsonDocument.Parse(headerBytes.ToArray());
            var header = document.RootElement;
            if (!Enum.TryParse<SongSourceKind>(text(header, "source"), out var source) || source != expected.Source
                || text(header, "song") != expected.StableId || text(header, "title") is not { Length: > 0 } primary)
                return null;
            var charts = new List<PackagedChart>();
            foreach (var entry in header.GetProperty("charts").EnumerateArray())
            {
                if (charts.Count == 5 || text(entry, "id") is not { Length: > 0 } id || text(entry, "name") is not { Length: > 0 } name
                    || !entry.GetProperty("course").TryGetInt32(out var courseIndex) || (uint)courseIndex > 4
                    || charts.Any(chart => (int)chart.Course == courseIndex || chart.StableId == id)
                    || !entry.GetProperty("duration_ms").TryGetInt64(out var durationMs))
                    return null;
                int? level = entry.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsed) && parsed is >= 0 and <= 99
                    ? parsed : null;
                var (chart, course) = ChartHash.Deserialize(readBlock(span, ref position, SongTransfer.MaxBytes).ToArray(), source);
                if ((int)course != courseIndex)
                    return null;
                var duration = TimeSpan.FromMilliseconds(durationMs);
                if (duration < TimeSpan.Zero || duration > MaxLength || chart.Duration > MaxLength)
                    return null;
                charts.Add(new PackagedChart(id, course, level, name,
                    chart with { Key = new ChartKey(expected, id), Duration = duration > chart.Duration ? duration : chart.Duration }));
            }
            var audio = readBlock(span, ref position, SongTransfer.MaxBytes).ToArray();
            if (charts.Count == 0 || audio.Length == 0 || position != span.Length)
                return null;
            var preview = header.TryGetProperty("preview_ms", out var previewValue) && previewValue.TryGetInt64(out var previewMs)
                && previewMs >= 0 && previewMs < MaxLength.TotalMilliseconds ? TimeSpan.FromMilliseconds(previewMs) : (TimeSpan?)null;
            return new SongPackage(expected, new SongTitle(primary, text(header, "title_ja"), text(header, "title_en")),
                text(header, "artist"), text(header, "subtitle"), preview, charts, audio);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException
            or ArgumentException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The song as the host's game has it: every course's chart (the one-player one) and the audio. Null when it can't be
    /// sent: not a custom song, no audio, or too big.
    /// </summary>
    public static async Task<SongPackage?> FromAsync(SongDescriptor song, ICatalogAssetResolver assets, IPlayableChartProvider charts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Key.Source is not (SongSourceKind.Tja or SongSourceKind.OsuLazer) || song.AudioAsset is not { } audioAsset)
            return null;
        var packaged = new List<PackagedChart>();
        foreach (var course in Enum.GetValues<TaikoCourse>())
        {
            if (song.Charts.FirstOrDefault(chart => chart.Course == course) is not { } descriptor)
                continue;
            var chart = await charts.LoadChartAsync(descriptor.Key, descriptor.ChartAsset, cancellationToken).ConfigureAwait(false);
            packaged.Add(new PackagedChart(descriptor.Key.StableId, course, descriptor.Level, descriptor.DifficultyName, chart));
        }
        await using var input = await assets.OpenReadAsync(audioAsset, cancellationToken).ConfigureAwait(false);
        using var audio = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (audio.Length + read > SongTransfer.MaxBytes)
                return null;
            audio.Write(buffer, 0, read);
        }
        var package = new SongPackage(song.Key, song.Title, song.Artist, song.Subtitle, song.PreviewStart, packaged, audio.ToArray());
        return packaged.Count == 0 ? null : package;
    }

    private static string? text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= MaxText } found ? found : null;

    private static void writeBlock(Stream output, byte[] block)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, block.Length);
        output.Write(length);
        output.Write(block);
    }

    private static ReadOnlySpan<byte> readBlock(ReadOnlySpan<byte> data, ref int position, int maximum)
    {
        if (data.Length - position < 4)
            throw new InvalidDataException("A song package ends early.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        if (length < 0 || length > maximum || length > data.Length - position - 4)
            throw new InvalidDataException("A song package block is the wrong size.");
        position += 4 + length;
        return data.Slice(position - length, length);
    }
}
