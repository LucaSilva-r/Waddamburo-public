using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.SongSelect;

[Flags]
public enum SongCourseBits
{
    None = 0,
    Easy = 1 << 0,
    Normal = 1 << 1,
    Hard = 1 << 2,
    Oni = 1 << 3,
    Ura = 1 << 4,
    All = Easy | Normal | Hard | Oni | Ura,
}

[Flags]
public enum SongCategoryPresentation
{
    None = 0,
    AlwaysVisible = 1 << 0,
    HideSongCount = 1 << 1,
}

public sealed record SongSelectSong(
    SongDescriptor Descriptor,
    SongCourseBits AvailableCourses,
    ImmutableArray<int?> Levels)
{
    public bool HasCourse(TaikoCourse course) => (AvailableCourses & bit(course)) != 0;

    public int? Level(TaikoCourse course) => Levels[(int)course];

    private static SongCourseBits bit(TaikoCourse course) => (SongCourseBits)(1 << (int)course);
}

public sealed record SongSelectCategory(
    CategoryKey Key,
    string Name,
    string AuthoredLabel,
    SongCategoryPresentation Presentation,
    ImmutableArray<SongSelectSong> Songs);

/// <summary>Source-neutral, revision-pinned data consumed by the authored Song Select host.</summary>
public sealed class SongSelectCatalogView
{
    public SongSelectCatalogView(SongCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Revision = snapshot.Revision;
        Categories = [.. snapshot.Categories.Select(category => new SongSelectCategory(
            category.Key,
            category.Name,
            authoredLabel(category.Name),
            SongCategoryPresentation.AlwaysVisible,
            [.. category.Songs.Select(key => createSong(snapshot.Songs[key]))]))];
    }

    public long Revision { get; }

    public ImmutableArray<SongSelectCategory> Categories { get; }

    public bool TryGetSong(int category, int song, out SongSelectSong result)
    {
        if ((uint)category < (uint)Categories.Length
            && (uint)song < (uint)Categories[category].Songs.Length)
        {
            result = Categories[category].Songs[song];
            return true;
        }
        result = null!;
        return false;
    }

    private static SongSelectSong createSong(SongDescriptor descriptor)
    {
        var bits = SongCourseBits.None;
        var levels = new int?[5];
        foreach (var chart in descriptor.Charts)
        {
            if (chart.Course is not { } course)
                continue;
            bits |= (SongCourseBits)(1 << (int)course);
            if (chart.Level is { } level && (levels[(int)course] is null || level > levels[(int)course]))
                levels[(int)course] = level;
        }
        return new SongSelectSong(descriptor, bits, [.. levels]);
    }

    private static string authoredLabel(string category) => category switch
    {
        "Pop" or "J-POP" => "J-POP",
        "Anime" => "アニメ",
        "Vocaloid" => "ボーカロイド",
        "Children and Folk" or "Kids" => "童謡",
        "Variety" => "バラエティ",
        "Classical" => "クラシック",
        "Game Music" => "ゲームミュージック",
        "Namco Original" => "ナムコオリジナル",
        _ => "イベント",
    };
}
