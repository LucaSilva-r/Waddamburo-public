using System.Globalization;
using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

internal static class TjaMetadataReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static TjaMetadataReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static TjaMetadata Read(ReadOnlySpan<byte> bytes)
    {
        var text = Decode(bytes);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var charts = new List<TjaChartMetadata>();
        string? course = null;
        string? level = null;
        var started = false;
        var occurrences = new Dictionary<(TaikoCourse Course, string Player), int>();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("#START", StringComparison.OrdinalIgnoreCase))
            {
                started = true;
                var rawPlayer = line[6..].Trim().ToUpperInvariant();
                var player = rawPlayer switch
                {
                    "1P" => "P1",
                    "2P" => "P2",
                    _ => rawPlayer,
                };
                if (player is not ("" or "P1" or "P2"))
                {
                    charts.Add(new TjaChartMetadata(null, $"Invalid #START {rawPlayer}", null, rawPlayer, 0));
                    continue;
                }
                if (!TryParseCourse(course, out var parsedCourse))
                {
                    charts.Add(new TjaChartMetadata(null, course ?? "Missing COURSE", null, player, 0));
                    continue;
                }

                var key = (parsedCourse, player);
                occurrences.TryGetValue(key, out var occurrence);
                occurrences[key] = ++occurrence;
                charts.Add(new TjaChartMetadata(
                    parsedCourse,
                    courseName(parsedCourse, player),
                    tryParseLevel(level),
                    player,
                    occurrence));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("COURSE", StringComparison.OrdinalIgnoreCase))
            {
                course = value;
                level = null;
            }
            else if (name.Equals("LEVEL", StringComparison.OrdinalIgnoreCase))
                level = value;

            if (!started)
                headers.TryAdd(name, value);
        }

        return new TjaMetadata(headers, charts);
    }

    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return StrictUtf8.GetString(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return shiftJis.GetString(bytes);
        }
    }

    internal static string StripComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    internal static bool TryParseCourse(string? value, out TaikoCourse course)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "3" or "ONI":
                course = TaikoCourse.Oni;
                return true;
            case "0" or "EASY":
                course = TaikoCourse.Easy;
                return true;
            case "1" or "NORMAL":
                course = TaikoCourse.Normal;
                return true;
            case "2" or "HARD":
                course = TaikoCourse.Hard;
                return true;
            case "4" or "EDIT" or "URA":
                course = TaikoCourse.Ura;
                return true;
            default:
                course = default;
                return false;
        }
    }

    private static int? tryParseLevel(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            ? Math.Clamp(level, 1, 10)
            : null;

    private static string courseName(TaikoCourse course, string player) =>
        string.IsNullOrWhiteSpace(player) ? course.ToString() : $"{course} ({player})";
}

internal sealed record TjaMetadata(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<TjaChartMetadata> Charts)
{
    public string? Get(string name) => Headers.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
}

internal sealed record TjaChartMetadata(
    TaikoCourse? Course,
    string DifficultyName,
    int? Level,
    string Player,
    int Occurrence);
