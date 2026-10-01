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
    /// <summary>GenreResource.FLAG_FOLDER_END: a folder without songs that decides at once (the mode switch).</summary>
    FolderEnd = 1 << 2,
}

/// <summary>Which authored song select the view feeds.</summary>
public enum SongSelectMode { Normal, Waiwai }

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
    SongBoardTextureStyle BoardStyle,
    ImmutableArray<SongSelectSong> Songs,
    SongSourceKind? Link = null,
    SongBrowseControl? Control = null);

/// <summary>A search's query and matching songs, in order (any library).</summary>
public sealed record SongSearchResults(string Query, IReadOnlyList<SongKey> Songs);

/// <summary>A spine that cycles the library's folder grouping or song order (decides at once).</summary>
public enum SongBrowseControl { Group, Sort }

/// <summary>Source-neutral, revision-pinned data consumed by the authored Song Select host.</summary>
public sealed class SongSelectCatalogView
{
    /// <summary>The label of the folder that moves between normal and Waiwai song select (GenreResource id 20).</summary>
    public const string ModeSwitchLabel = "通常とワイワイ移動";

    /// <summary>The spine art of library folders (its name goes in the movie's feature_board_* slots).</summary>
    public const string FeatureLabel = "イベント";

    /// <summary>The how-to-play folder (GenreResource id 0): deciding it plays the tutorial movie.</summary>
    public const string TutorialLabel = "あそびかた説明";

    /// <summary>The favourites folder's art (GenreResource.FAVORITE).</summary>
    public const string FavouritesLabel = "お気に入り";

    /// <summary>
    /// <paramref name="library"/> lists only that source's categories (null: every source);
    /// <paramref name="links"/> appends a folder per other library that switches Song Select to it.
    /// <paramref name="mode"/> Waiwai lists only the songs with a Waiwai layout (empty genres dropped).
    /// <paramref name="modeSwitch"/> appends the folder to the other song select, which the game shows
    /// with two players (traced last in both selects).
    /// <paramref name="favourites"/> lists the marked songs of the shown library in a folder first
    /// (where the game assigns お気に入り, after おすすめ); see <see cref="RefreshFavourites"/>.
    /// <paramref name="tutorial"/> puts the how-to-play folder first (traced: normal select, until watched).
    /// <paramref name="search"/> lists a search's songs (any library) in a folder before the others.
    /// <paramref name="browse"/> (the osu!lazer library only) groups its songs into folders and sorts
    /// them, with a Group and a Sort spine after the folders to change either.
    /// </summary>
    // ponytail: Waiwai's "how to play" folder (id 21, before the switch; waiwai_tutorial) is left out, untraced.
    public SongSelectCatalogView(SongCatalogSnapshot snapshot, SongSelectMode mode = SongSelectMode.Normal,
        bool modeSwitch = false, SongSourceKind? library = null, IEnumerable<SongSourceKind>? links = null,
        IEnumerable<SongKey>? favourites = null, bool tutorial = false, SongBrowse? browse = null,
        SongSearchResults? search = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Revision = snapshot.Revision;
        Mode = mode;
        _snapshot = snapshot;
        _library = library;
        var favouriteFolder = favourites is null ? [] : new[]
        {
            new SongSelectCategory(new CategoryKey(library ?? SongSourceKind.Stock, "favourites"), "Favourites",
                FavouritesLabel, SongCategoryPresentation.AlwaysVisible, boardStyle(""), marked(favourites)),
        };
        var tutorialFolder = !tutorial || mode != SongSelectMode.Normal ? [] : new[]
        {
            new SongSelectCategory(new CategoryKey(SongSourceKind.Stock, "tutorial"), "How to Play", TutorialLabel,
                SongCategoryPresentation.AlwaysVisible | SongCategoryPresentation.FolderEnd, boardStyle(""), []),
        };
        var listed = library == SongSourceKind.OsuLazer && browse is not null
            ? browsed(snapshot, browse)
            : snapshot.Categories
                .Where(category => library is null || category.Key.Source == library)
                .Select(category => (category.Key, category.Name, Songs: category.Songs.Select(key => snapshot.Songs[key])));
        var searchFolder = search is null ? [] : new[]
        {
            new SongSelectCategory(SearchKey, $"Search: {search.Query}", FeatureLabel, SongCategoryPresentation.AlwaysVisible,
                boardStyle(""), [.. search.Songs.Where(snapshot.Songs.ContainsKey).Select(key => snapshot.Songs[key])
                    .Where(song => mode == SongSelectMode.Normal || song.WaiwaiComposition is not null)
                    .Select(createSong)]),
        };
        var categories = searchFolder.Concat(tutorialFolder).Concat(favouriteFolder).Concat(listed
            .Select(category => new SongSelectCategory(
            category.Key,
            category.Name,
            authoredLabel(category.Name),
            SongCategoryPresentation.AlwaysVisible,
            boardStyle(category.Name),
            [.. category.Songs
                .Where(song => mode == SongSelectMode.Normal || song.WaiwaiComposition is not null)
                .Select(createSong)]))
            .Where(category => mode == SongSelectMode.Normal || !category.Songs.IsEmpty))
            .Concat(library == SongSourceKind.OsuLazer && browse is not null ? new[]
            {
                control(SongBrowseControl.Group, $"Group: {SongBrowse.Name(browse.Group)}"),
                control(SongBrowseControl.Sort, $"Sort: {SongBrowse.Name(browse.Sort)}"),
            } : [])
            .Concat((links ?? []).Select(link => new SongSelectCategory(new CategoryKey(link, "library"),
                LibraryName(link), FeatureLabel, SongCategoryPresentation.AlwaysVisible | SongCategoryPresentation.FolderEnd,
                boardStyle(""), [], link)));
        if (modeSwitch)
            categories = categories.Append(new SongSelectCategory(new CategoryKey(SongSourceKind.Stock, "mode-switch"),
                mode == SongSelectMode.Waiwai ? "To Normal" : "To Waiwai", ModeSwitchLabel,
                SongCategoryPresentation.FolderEnd, boardStyle(""), []));
        Categories = [.. categories];
    }

    public SongSelectMode Mode { get; }

    /// <summary>The search results folder's key (see the constructor's search).</summary>
    public static readonly CategoryKey SearchKey = new(SongSourceKind.Stock, "search");

    // The osu!lazer library's folders by the browse's grouping; its collections come from the provider's
    // collection categories. A grouping with no folder (no collections) lists every song.
    private static IEnumerable<(CategoryKey Key, string Name, IEnumerable<SongDescriptor> Songs)> browsed(
        SongCatalogSnapshot snapshot, SongBrowse browse)
    {
        var songs = snapshot.Songs.Values.Where(static song => song.Key.Source == SongSourceKind.OsuLazer).ToArray();
        var collections = snapshot.Categories
            .Where(static category => category.Key.Source == SongSourceKind.OsuLazer
                && category.Key.StableId.StartsWith("collection:", StringComparison.Ordinal))
            .Select(category => (category.Name, (IReadOnlyList<SongDescriptor>)[.. category.Songs.Select(key => snapshot.Songs[key])]));
        var folders = browse.Folders(songs, collections, DateTimeOffset.UtcNow);
        if (folders.Count == 0)
            folders = (browse with { Group = SongGroupMode.All }).Folders(songs, [], DateTimeOffset.UtcNow);
        return folders.Select(folder => (new CategoryKey(SongSourceKind.OsuLazer, $"{browse.Group}:{folder.Name}"),
            folder.Name, folder.Songs.AsEnumerable()));
    }

    /// <summary>
    /// A library's genre art for boards in a folder of mixed songs (search results): stock J-POP blue,
    /// custom TJA Variety green, osu!lazer Kids pink, Nijiiro おすすめ.
    /// </summary>
    public static string SourceLabel(SongSourceKind source) => source switch
    {
        SongSourceKind.Tja => "バラエティ",
        SongSourceKind.OsuLazer => "童謡",
        SongSourceKind.Nijiiro => "おすすめ",
        _ => "J-POP",
    };

    /// <summary>The title outline a song's board gets: its folder's, or in the search folder its library's.</summary>
    public SongBoardTextureStyle BoardStyle(int category, SongSelectSong song) =>
        Categories[category].Key == SearchKey ? boardStyle(song.Descriptor.Key.Source switch
        {
            SongSourceKind.Tja => "Variety",
            SongSourceKind.OsuLazer => "Kids",
            SongSourceKind.Nijiiro => "",
            _ => "J-POP",
        }) : Categories[category].BoardStyle;

    private static SongSelectCategory control(SongBrowseControl control, string name) =>
        new(new CategoryKey(SongSourceKind.OsuLazer, $"control:{control}"), name, FeatureLabel,
            SongCategoryPresentation.AlwaysVisible | SongCategoryPresentation.FolderEnd, boardStyle(""), [], Control: control);

    private readonly SongCatalogSnapshot _snapshot;
    private readonly SongSourceKind? _library;

    /// <summary>Index of the favourites folder, or -1.</summary>
    public int FavouritesCategory => Categories.ToList().FindIndex(static category => category.AuthoredLabel == FavouritesLabel);

    /// <summary>Relists the favourites folder (the movie's song count must follow); its song count, or null without one.</summary>
    public int? RefreshFavourites(IEnumerable<SongKey> favourites)
    {
        var index = FavouritesCategory;
        if (index < 0)
            return null;
        Categories = Categories.SetItem(index, Categories[index] with { Songs = marked(favourites) });
        return Categories[index].Songs.Length;
    }

    // The marked songs of the listed library.
    private ImmutableArray<SongSelectSong> marked(IEnumerable<SongKey> favourites) => [.. favourites
        .Where(key => (_library is null || key.Source == _library) && _snapshot.Songs.ContainsKey(key))
        .Select(key => _snapshot.Songs[key])
        .Where(song => Mode == SongSelectMode.Normal || song.WaiwaiComposition is not null)
        .Select(createSong)];

    /// <summary>Index of the folder that switches to <paramref name="library"/>, or -1.</summary>
    public int LinkCategory(SongSourceKind library) => Categories.ToList().FindIndex(category => category.Link == library);

    /// <summary>A song's folder and position (null: not listed).</summary>
    public (int Category, int Song)? Find(CategoryKey category, SongKey song)
    {
        var index = Categories.ToList().FindIndex(entry => entry.Key == category);
        var position = index < 0 ? -1 : Categories[index].Songs.ToList().FindIndex(entry => entry.Descriptor.Key == song);
        return position < 0 ? null : (index, position);
    }

    public static string LibraryName(SongSourceKind library) => library switch
    {
        SongSourceKind.Stock => "ORIGINAL",
        SongSourceKind.Nijiiro => "NIJIIRO",
        SongSourceKind.Tja => "CUSTOM TJA",
        SongSourceKind.OsuLazer => "OSU! LAZER",
        _ => library.ToString(),
    };

    /// <summary>Index of the mode-switch folder, or -1.</summary>
    public int ModeSwitchCategory => Categories.ToList().FindIndex(static category => category.AuthoredLabel == ModeSwitchLabel);

    /// <summary>Index of the how-to-play folder, or -1.</summary>
    public int TutorialCategory => Categories.ToList().FindIndex(static category => category.AuthoredLabel == TutorialLabel);

    public long Revision { get; }

    public ImmutableArray<SongSelectCategory> Categories { get; private set; }

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
        _ => FeatureLabel,
    };

    private static SongBoardTextureStyle boardStyle(string category) => new(category switch
    {
        "Pop" or "J-POP" => 0x015059U,
        "Children and Folk" or "Kids" => 0xbb015dU,
        "Variety" => 0x374702U,
        "Anime" => 0x9e4309U,
        "Classical" => 0x734f02U,
        "Game Music" => 0x4b1a70U,
        "Namco Original" => 0xa22302U,
        "Vocaloid" => 0x596585U,
        _ => 0x141428U,
    });
}
