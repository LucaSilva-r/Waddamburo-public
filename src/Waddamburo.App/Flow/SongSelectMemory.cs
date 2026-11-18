using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Flow;

/// <summary>
/// What the next Song Select starts on: the library listed, the osu! grouping, the last song picked, a search's
/// results, an online song, and the spine or library just left. Home keeps it across credits; a cabinet's new
/// credit forgets the last song (<see cref="ForgetLastSong"/>).
/// </summary>
internal sealed class SongSelectMemory(SongCatalogSnapshot catalog, Func<ArcadeSettings> arcadeSettings)
{
    /// <summary>The song library normal Song Select lists (home mode switches between them).</summary>
    public SongSourceKind Library
    {
        get => _library ??= Libraries().FirstOrDefault();
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
        if (Libraries().Contains(category.Key.Source))
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
    public void ForgetLastSong() => _lastPick = null;

    /// <summary>
    /// Where a Song Select listing <paramref name="view"/> starts (category, song; -1: its first), after the one
    /// that closed picked <paramref name="picked"/>: back from a library, on the folder that leads to it; otherwise on
    /// the last song played, when the view lists it. The one-shot starts (a spine, a library, a search) are used up.
    /// </summary>
    public (int Category, int Song) Start(SongSelectCatalogView view, (CategoryKey Category, SongKey Song)? picked)
    {
        _lastPick = picked ?? _lastPick;
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
        return (start, startSong);
    }

    /// <summary>
    /// Home mode keeps each source in its own library (arcade lists the stock songs as the game does); one source
    /// needs no switching (empty).
    /// </summary>
    public SongSourceKind[] Libraries()
    {
        var sources = catalog.Categories.Select(static category => category.Key.Source).Distinct().Order().ToArray();
        return arcadeSettings().Home && sources.Length > 1 ? sources : [];
    }
}
