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
    /// subtitles, artist or mapper.
    /// </summary>
    public static bool Matches(SongDescriptor song, string query)
    {
        ArgumentNullException.ThrowIfNull(song);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return false;
        var text = string.Join('\n', new[]
        {
            song.Title.Primary, song.Title.English, song.Title.Japanese, song.Subtitle, song.EnglishSubtitle,
            song.Artist, song.Creator,
        }.Where(static value => value is not null));
        return words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

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
