using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

/// <summary>
/// An .lrc lyrics file: lines "[mm:ss.xx]text" (several time tags may share a line), an optional "[offset:±ms]" tag
/// (positive: the lines come sooner). Times are the audio's; <c>chartOffset</c> (the chart's OFFSET) turns them into
/// chart time. Other tags ([ar:], [ti:] ...) and lines without a time are skipped.
/// </summary>
internal static partial class LrcLyrics
{
    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    public static ImmutableArray<ChartLyric> Read(string text, TimeSpan chartOffset)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var shift = lines.Select(static line => OffsetTag().Match(line.Trim())).FirstOrDefault(static match => match.Success) is { } offset
            ? TimeSpan.FromMilliseconds(-int.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero;
        var lyrics = new List<ChartLyric>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var tags = TimeTag().Matches(line);
            if (tags.Count == 0 || tags[0].Index != 0)
                continue;
            var words = line[(tags[^1].Index + tags[^1].Length)..].Trim();
            foreach (Match tag in tags)
            {
                var seconds = int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(tag.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lyrics.Add(new ChartLyric(TimeSpan.FromSeconds(seconds) + shift + chartOffset, words));
            }
        }
        return [.. lyrics.OrderBy(static lyric => lyric.Time)];
    }
}
