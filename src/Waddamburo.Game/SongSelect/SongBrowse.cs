using System.Globalization;
using Waddamburo.Catalog;

namespace Waddamburo.Game.SongSelect;

/// <summary>How a library's songs are split into folders (after osu!'s song select groups).</summary>
public enum SongGroupMode { All, Title, Artist, Creator, Difficulty, Collection, DateAdded, Bpm, Length }

/// <summary>The order of the songs inside each folder.</summary>
public enum SongSortMode { Title, Artist, Creator, Difficulty, DateAdded, Bpm, Length }

/// <summary>A library's folder grouping and song order, cycled from the song select's own spines.</summary>
public sealed record SongBrowse(SongGroupMode Group = SongGroupMode.Title, SongSortMode Sort = SongSortMode.Title)
{
    /// <summary>
    /// The most songs one folder holds: its count label has three digits, so a bigger group is split
    /// into numbered parts.
    /// </summary>
    public const int MaxFolderSongs = 999;

    public SongBrowse NextGroup() => this with { Group = next(Group) };

    public SongBrowse NextSort() => this with { Sort = next(Sort) };

    public static string Name(SongGroupMode mode) => mode switch
    {
        SongGroupMode.All => "None",
        SongGroupMode.Creator => "Mapper",
        SongGroupMode.Collection => "Collections",
        SongGroupMode.DateAdded => "Date Added",
        SongGroupMode.Bpm => "BPM",
        _ => mode.ToString(),
    };

    public static string Name(SongSortMode mode) => mode switch
    {
        SongSortMode.Creator => "Mapper",
        SongSortMode.DateAdded => "Date Added",
        SongSortMode.Bpm => "BPM",
        _ => mode.ToString(),
    };

    /// <summary>
    /// The folders, in order, with their songs sorted. <paramref name="collections"/>: the player's
    /// collections (name, songs) for <see cref="SongGroupMode.Collection"/>; <paramref name="now"/>
    /// dates the Date Added groups.
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<SongDescriptor> Songs)> Folders(
        IReadOnlyCollection<SongDescriptor> songs,
        IEnumerable<(string Name, IReadOnlyList<SongDescriptor> Songs)> collections,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(songs);
        IEnumerable<(string Name, IEnumerable<SongDescriptor> Songs)> groups = Group switch
        {
            SongGroupMode.All => [("All Songs", songs)],
            SongGroupMode.Collection => collections.Select(static collection => (collection.Name, collection.Songs.AsEnumerable())),
            _ => songs.SelectMany(song => keys(song, now).Select(key => (key, song)))
                .GroupBy(static pair => pair.key)
                .OrderBy(static group => group.Key.Order).ThenBy(static group => group.Key.Name, StringComparer.Ordinal)
                .Select(static group => (group.Key.Name, group.Select(static pair => pair.song))),
        };
        var folders = new List<(string, IReadOnlyList<SongDescriptor>)>();
        foreach (var (name, members) in groups)
        {
            var sorted = sort(members.Distinct()).ToArray();
            var parts = (sorted.Length + MaxFolderSongs - 1) / MaxFolderSongs;
            for (var part = 0; part < parts; part++)
                folders.Add((parts == 1 ? name : $"{name} {part + 1}/{parts}",
                    sorted[(part * MaxFolderSongs)..Math.Min(sorted.Length, (part + 1) * MaxFolderSongs)]));
        }
        return folders;
    }

    /// <summary>
    /// Whether every word of <paramref name="query"/> appears (ignoring case) in the song's titles,
    /// subtitles, artist or mapper, as written or with their kana in romaji (<see cref="Romaji"/>), and every filter holds
    /// (<see cref="Filter"/>: <c>stars&gt;=8</c>, <c>oni&gt;=9</c>, <c>ura&gt;0</c>, <c>bpm&gt;180</c>, <c>length&lt;2:30</c>,
    /// <c>artist=</c>, <c>mapper=</c>, <c>title=</c>, and with <paramref name="crownOf"/> the player's crowns: <c>crown=fc</c>,
    /// <c>oni=notcleared</c>; <c>source=osu,tja</c>).
    /// Filters and words all have to hold (a comma list in one filter is any of its values).
    /// </summary>
    public static bool Matches(SongDescriptor song, string query, Func<SongChartDescriptor, Scores.TaikoCrown>? crownOf = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filters = tokens.Select(token => (Token: token, Holds: Filter(token, crownOf))).ToArray();
        var words = filters.Where(static filter => filter.Holds is null).Select(static filter => filter.Token).ToArray();
        if (tokens.Length == 0 || !filters.All(filter => filter.Holds?.Invoke(song) ?? true))
            return false;
        if (words.Length == 0)
            return true;
        var text = string.Join('\n', new[]
        {
            song.Title.Primary, song.Title.English, song.Title.Japanese, song.Subtitle, song.EnglishSubtitle,
            song.Artist, song.Creator,
        }.Where(static value => value is not null));
        // ponytail: romanised per query, each song every keystroke; cache it on the song if search gets slow.
        var romaji = Romaji.Fold(Romaji.FromKana(text));
        return words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)
            || romaji.Contains(Romaji.Fold(word), StringComparison.Ordinal));
    }

    /// <summary>
    /// A search filter token (osu!'s style: key, operator <c>= : &gt; &lt; &gt;= &lt;=</c>, value) as a test on a song;
    /// null when the token isn't one (it is then a word to find). Levels: <c>stars</c> any chart's, a course name
    /// (easy, normal, hard, oni, ura) that course's (a song without the course never matches). Length in seconds or m:ss. Crowns (with
    /// <paramref name="crownOf"/>): <c>crown</c> any chart's, a course name that course's; notcleared, cleared, fc, dondaful.
    /// </summary>
    public static Func<SongDescriptor, bool>? Filter(string token, Func<SongChartDescriptor, Scores.TaikoCrown>? crownOf = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var at = token.IndexOfAny(['=', ':', '>', '<']);
        if (at <= 0 || at == token.Length - 1)
            return null;
        var key = token[..at].ToLowerInvariant();
        var op = token[at + 1] == '=' && token[at] is '>' or '<' ? token.Substring(at, 2) : token[at].ToString();
        var value = token[(at + op.Length)..];
        if (value.Length == 0)
            return null;
        bool compare(double actual, double wanted) => op switch
        {
            ">" => actual > wanted, "<" => actual < wanted, ">=" => actual >= wanted, "<=" => actual <= wanted,
            _ => Math.Abs(actual - wanted) < 0.5,
        };
        bool number(out double wanted) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out wanted);
        bool contains(string? text) => op is "=" or ":" && text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
        TaikoCourse? course = key switch
        {
            "easy" or "kantan" => TaikoCourse.Easy, "normal" or "futsuu" => TaikoCourse.Normal,
            "hard" or "muzukashii" => TaikoCourse.Hard, "oni" => TaikoCourse.Oni, "ura" or "edit" => TaikoCourse.Ura,
            _ => null,
        };
        // Only songs that have the course: ura>0 is "has an ura", and ura<5 leaves out songs without one.
        if (course is { } wantedCourse && number(out var level))
            return song => song.Charts.Any(chart => chart.Course == wantedCourse && compare(chart.Level ?? 0, level));
        // Crowns: none < cleared < full combo < Dondaful, so crown>=fc takes both gold ones.
        if (crownOf is not null && (course is not null || key == "crown") && crown(value) is { } wantedCrown)
            return song => song.Charts.Any(chart => (course is null || chart.Course == course)
                && compare((int)crownOf(chart), (int)wantedCrown));
        return key switch
        {
            "stars" or "star" or "level" when number(out var stars) =>
                song => song.Charts.Any(chart => compare(chart.Level ?? 0, stars)),
            "bpm" when number(out var bpm) => song => song.Bpm is { } actual && compare(actual, bpm),
            "length" or "len" when seconds(value) is { } length => song => song.Length is { } actual && compare(actual.TotalSeconds, length),
            "artist" => song => contains(song.Artist),
            "mapper" or "creator" or "charter" => song => contains(song.Creator),
            "title" => song => contains(song.Title.Primary) || contains(song.Title.English) || contains(song.Title.Japanese),
            // source=osu,tja: any of the listed libraries (osu = lazer and stable).
            "source" or "src" or "library" when op is "=" or ":" && sources(value) is { Count: > 0 } wanted =>
                song => wanted.Contains(song.Key.Source),
            _ => null,
        };
    }

    private static HashSet<SongSourceKind>? sources(string value)
    {
        var wanted = new HashSet<SongSourceKind>();
        foreach (var name in value.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            SongSourceKind[] kinds = name switch
            {
                "osu" => [SongSourceKind.OsuLazer, SongSourceKind.OsuStable],
                "lazer" or "osulazer" => [SongSourceKind.OsuLazer],
                "stable" or "osustable" => [SongSourceKind.OsuStable],
                "tja" => [SongSourceKind.Tja],
                "nijiiro" or "niji" => [SongSourceKind.Nijiiro],
                "stock" or "official" or "green" => [SongSourceKind.Stock],
                _ => [],
            };
            if (kinds.Length == 0)
                return null; // an unknown name: not a filter, a word
            wanted.UnionWith(kinds);
        }
        return wanted;
    }

    private static Scores.TaikoCrown? crown(string value) => value.ToLowerInvariant() switch
    {
        "none" or "notcleared" or "uncleared" or "failed" => Scores.TaikoCrown.None,
        "clear" or "cleared" or "silver" => Scores.TaikoCrown.Clear,
        "fc" or "fullcombo" or "gold" => Scores.TaikoCrown.FullCombo,
        "dfc" or "dondaful" or "donderful" or "zenryo" or "rainbow" => Scores.TaikoCrown.Dondaful,
        _ => null,
    };

    // "150" or "2:30", in seconds.
    private static double? seconds(string value) =>
        value.Split(':') is [var minutes, var rest] && int.TryParse(minutes, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(rest, CultureInfo.InvariantCulture, out var s) ? m * 60 + s
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total) ? total : null;

    private IOrderedEnumerable<SongDescriptor> sort(IEnumerable<SongDescriptor> songs)
    {
        var ordered = Sort switch
        {
            SongSortMode.Artist => songs.OrderBy(static song => song.Artist ?? "", StringComparer.OrdinalIgnoreCase),
            SongSortMode.Creator => songs.OrderBy(static song => song.Creator ?? "", StringComparer.OrdinalIgnoreCase),
            SongSortMode.Difficulty => songs.OrderBy(stars),
            SongSortMode.DateAdded => songs.OrderByDescending(static song => song.DateAdded ?? DateTimeOffset.MinValue),
            SongSortMode.Bpm => songs.OrderBy(static song => song.Bpm ?? 0),
            SongSortMode.Length => songs.OrderBy(static song => song.Length ?? TimeSpan.Zero),
            _ => songs.OrderBy(title, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ThenBy(title, StringComparer.OrdinalIgnoreCase).ThenBy(static song => song.Key.StableId, StringComparer.Ordinal);
    }

    // A song's folder keys (several for Difficulty: one per chart level), with their folder order.
    private IEnumerable<(int Order, string Name)> keys(SongDescriptor song, DateTimeOffset now) => Group switch
    {
        SongGroupMode.Title => [alphabetical(title(song))],
        SongGroupMode.Artist => [alphabetical(song.Artist)],
        SongGroupMode.Creator => [alphabetical(song.Creator)],
        SongGroupMode.Difficulty => song.Charts.Select(static chart => chart.Level ?? 0).Distinct()
            .Select(static level => (level, level == 0 ? "No stars" : $"★{level}")),
        SongGroupMode.DateAdded => [date(song.DateAdded, now)],
        SongGroupMode.Bpm => [bpm(song.Bpm)],
        SongGroupMode.Length => [length(song.Length)],
        _ => [(0, "All Songs")],
    };

    private static string title(SongDescriptor song) => song.Title.English ?? song.Title.Primary;

    private static int stars(SongDescriptor song) => song.Charts.Max(static chart => chart.Level ?? 0);

    // osu!: "0-9", the letter, or other symbols (non-Latin titles land there too).
    private static (int, string) alphabetical(string? name)
    {
        var first = string.IsNullOrWhiteSpace(name) ? ' ' : char.ToUpperInvariant(name.TrimStart()[0]);
        return first switch
        {
            >= '0' and <= '9' => (-1, "0-9"),
            >= 'A' and <= 'Z' => (first - 'A', first.ToString()),
            _ => (int.MaxValue, "Other"),
        };
    }

    private static (int, string) date(DateTimeOffset? added, DateTimeOffset now)
    {
        if (added is not { } date)
            return (int.MaxValue, "Unknown");
        var days = (now.Date - date.ToUniversalTime().Date).TotalDays;
        return days switch
        {
            <= 0 => (0, "Today"),
            <= 1 => (1, "Yesterday"),
            <= 7 => (2, "Last week"),
            <= 30 => (3, "Last month"),
            <= 180 => ((int)days / 30 + 3, $"{(int)days / 30} months ago"),
            _ => (int.MaxValue - 1, "Over 6 months ago"),
        };
    }

    private static (int, string) bpm(double? bpm) => bpm switch
    {
        null or <= 0 => (int.MaxValue, "Unknown"),
        < 60 => (0, "Under 60 BPM"),
        > 300 => (301, "Over 300 BPM"),
        _ => ((int)Math.Ceiling(bpm.Value / 10) * 10, string.Create(CultureInfo.InvariantCulture,
            $"{(int)Math.Ceiling(bpm.Value / 10) * 10 - 10}-{(int)Math.Ceiling(bpm.Value / 10) * 10} BPM")),
    };

    private static (int, string) length(TimeSpan? length)
    {
        if (length is not { } value || value <= TimeSpan.Zero)
            return (int.MaxValue, "Unknown");
        var minutes = (int)Math.Ceiling(value.TotalMinutes);
        return minutes > 10 ? (11, "Over 10 minutes") : (minutes, minutes == 1 ? "1 minute or less" : $"{minutes} minutes or less");
    }

    private static T next<T>(T value) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        return values[(Array.IndexOf(values, value) + 1) % values.Length];
    }
}
