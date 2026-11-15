using Waddamburo.App.Online;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Hosting;

/// <summary>
/// What the next Song Select lists (the library, the osu! grouping and order, a search, an online song, the folders the
/// credit is offered) and where its cursor starts. Engine side: every skin's song select is built from it.
/// </summary>
internal sealed class SongSelectSetup(
    CreditState credit,
    SongCatalogSnapshot catalog,
    ISongBoardTextureService textures,
    ISongPreviewController previews,
    IPlayRequestSink playRequests,
    Func<ArcadeSettings> arcadeSettings)
{
    private CreditState Credit => credit;
    private ArcadeSettings arcade => arcadeSettings();

    /// <summary>A drum's play history as chart keys, newest first (this PC's plays and the server's); null: none.</summary>
    public Func<int, IReadOnlyList<string>?>? RecentCharts { get; init; }

    /// <summary>The settings' random-song spine (the movie's roll folder) is on.</summary>
    public Func<bool>? RandomSongFolder { get; init; }

    /// <summary>Home: the tutorial was watched once (kept in config.cfg); song select no longer offers it.</summary>
    public Func<bool>? HomeTutorialSeen { get; init; }

    /// <summary>Whether Song Select offers the Online folder.</summary>
    public Func<bool>? OnlineAvailable { get; init; }

    /// <summary>The player is in an online room: songs are played together, so no dan dojo.</summary>
    public Func<bool>? InOnlineRoom { get; init; }

    /// <summary>A song another player sent this session (Song Select lists it while it's the online song).</summary>
    public Func<SongKey, SongDescriptor?>? Received { get; init; }

    /// <summary>The songs marked favourite (a folder per library, a red coin icon on their boards).</summary>
    public SongFavourites? Favourites { get; init; }

    /// <summary>The dan dojo's courses (null: no such folder in this run).</summary>
    public DanCourses? Dan { get; set; }

    /// <summary>The song library normal Song Select lists (home mode switches between them).</summary>
    public SongSourceKind Library
    {
        get => _library ??= libraries().FirstOrDefault();
        set => (_libraryLeft, _library) = (Library, value);
    }
    private SongSourceKind? _library, _libraryLeft;

    /// <summary>The osu!lazer library's grouping and order (from the settings; its spines cycle them).</summary>
    public SongBrowse OsuBrowse { get; set; } = arcadeSettings().OsuBrowse;

    /// <summary>Cycles the grouping or order a spine names; the reloaded Song Select starts on that spine.</summary>
    public SongBrowse CycleBrowse(SongBrowseControl control)
    {
        _browseLeft = control;
        return OsuBrowse = control == SongBrowseControl.Group ? OsuBrowse.NextGroup() : OsuBrowse.NextSort();
    }
    private SongBrowseControl? _browseLeft;


    /// <summary>The next Song Select opens on this song (an online host's pick), in its own library.</summary>
    public void FocusSong(SongKey song)
    {
        if (catalog.Categories.FirstOrDefault(category => category.Songs.Contains(song)) is not { } category)
            return;
        if (libraries().Contains(category.Key.Source))
            _library = category.Key.Source;
        _focus = (category.Key, song);
        (_browseLeft, _searchShown) = (null, false);
    }

    private (CategoryKey Category, SongKey Song)? _focus;

    /// <summary>Song Select lists only this song (the online host's pick, for the guest's course), from any library; null: as usual.</summary>
    public SongKey? OnlineSong { get; private set; }

    public void ShowOnlineSong(SongKey? song)
    {
        OnlineSong = song;
        if (song is not null)
            (_browseLeft, _searchShown) = (null, false);
    }

    /// <summary>The next Song Select starts on the Online spine (the lobby closed over it).</summary>
    public void ReturnToOnline() => _browseLeft = SongBrowseControl.Online;

    /// <summary>The last search's results, listed first in Song Select (null: no search folder).</summary>
    public SongSearchResults? Search
    {
        get => _search;
        set => (_search, _searchShown) = (value, value is not null);
    }
    private SongSearchResults? _search;
    private bool _searchShown;
    private (CategoryKey Category, SongKey Song)? _lastPick;

    /// <summary>A new cabinet credit: Song Select no longer starts on the previous credit's song.</summary>
    public void ForgetLastSong()
    {
        _lastPick = null;
        _current?.ForgetPicked();
    }

    // Home mode keeps each source in its own library (arcade lists the stock songs as the game does);
    // one source needs no switching.
    private SongSourceKind[] libraries()
    {
        var sources = catalog.Categories.Select(static category => category.Key.Source).Distinct().Order().ToArray();
        return arcade.Home && sources.Length > 1 ? sources : [];
    }

    private SongSelectSession? _current;

    /// <summary>
    /// The next Song Select's session: its catalog view for the credit (mode, libraries, folders offered) and where
    /// its cursor starts (the online song, the search, the spine or library left, else the last song played).
    /// </summary>
    public SongSelectSession CreateSession(bool counting)
    {
        var linked = Credit.Waiwai ? Array.Empty<SongSourceKind>() : libraries();
        var view = new SongSelectCatalogView(catalog, Credit.Waiwai ? SongSelectMode.Waiwai : SongSelectMode.Normal,
            modeSwitch: Credit.TwoPlayers, library: linked.Length > 0 && OnlineSong is null ? Library : null,
            // AI battle too: its song select takes the custom folders (SongSelectGenrePatch), the other libraries' among them.
            links: linked.Where(library => library != Library), favourites: Favourites?.Songs, tutorial: HomeTutorialSeen?.Invoke() != true && !(Credit.AiBattle ? Credit.AiTutorialSeen : Credit.Waiwai ? Credit.WaiwaiTutorialSeen : Credit.TutorialSeen),
            browse: OsuBrowse, search: Search, // As on the game: no dan dojo for two players, or without an account (it works offline once its list is saved).
            danDojo: !Credit.AiBattle && Dan?.Available == true && !Credit.TwoPlayers && TaikoGuest.Profiles[Credit.PlayerSide] is not null
                && InOnlineRoom?.Invoke() != true,
            danSeries: arcade.Home ? Dan?.Series : null,
            online: !Credit.Waiwai && !Credit.AiBattle && !Credit.TwoPlayers && OnlineAvailable?.Invoke() == true, onlySong: OnlineSong, received: Received,
            courseStar: OnlineSong is null, rollFolder: RandomSongFolder?.Invoke() == true,
            recentCharts: Credit.TwoPlayers || OnlineSong is not null ? null : RecentCharts?.Invoke(Credit.PlayerSide));
        // Back from a library: the cursor starts on the folder that leads to it; otherwise on the last
        // song played, when this view lists it.
        _lastPick = _current?.Picked ?? _lastPick;
        if (_focus is { } focus)
            (_lastPick, _focus) = (focus, null);
        var (start, startSong) = (0, -1);
        if (OnlineSong is not null)
            (start, startSong) = (0, 0);
        else if (_searchShown)
            start = Math.Max(view.Categories.ToList().FindIndex(category => category.Key == SongSelectCatalogView.SearchKey), 0);
        else if (_browseLeft is { } control)
            start = Math.Max(view.Categories.ToList().FindIndex(category => category.Control == control), 0);
        else if (_libraryLeft is { } left)
            start = Math.Max(view.LinkCategory(left), 0);
        else if (_lastPick is { } pick && view.Find(pick.Category, pick.Song) is { } found)
            (start, startSong) = found;
        _libraryLeft = null;
        _browseLeft = null;
        _searchShown = false;
        return _current = new SongSelectSession(view, textures, previews, playRequests)
        {
            Counting = counting,
            StartCategory = start,
            StartSong = startSong,
            ForcePick = OnlineSong is not null,
        };
    }
}
