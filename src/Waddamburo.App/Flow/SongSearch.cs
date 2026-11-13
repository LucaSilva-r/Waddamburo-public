using System.Globalization;
using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select's search, a page of the overlay menu (the settings' and online lobby's look): Tab opens it, typed text
/// searches every library (words and filters, see <see cref="SongBrowse.Matches"/>) with a cursor that ←/→ move, ↑/↓ go
/// through the last searches (picking one fills the field), Enter lists the matches in a "Search: …" folder first and
/// Song Select reloads on it (<see cref="SongSelectReload"/>); Escape or Tab again closes it. The footer lists the keys
/// and filters. While it is open the movie gets no input (the drum keys type, so the menu's own input is not used).
/// </summary>
internal sealed class SongSearch(GameShell shell, SongCatalogSnapshot catalog) : IInputOverlay, IDisposable
{
    private const int HistoryLength = 5, ShownCharacters = 60;
    private readonly OverlayMenu _menu = new(shell.Application, shell.Options.ServiceFontPath ?? shell.Options.FontPath);
    private string _query = "";
    private int _caret, _tick;
    private List<string> _history = [];
    // Enter came before the search of what is typed finished: open its results when they arrive.
    private bool _openWhenDone;
    // The newest finished search (its query and songs), written by the background search, read on the game thread.
    private volatile SearchResult _result = new("", []);
    // Bumped by each keystroke (and Close): a search seeing a newer one stops.
    private int _generation;
    // The searching player's best crown per chart key (crown filters), read as the field opens.
    private IReadOnlyDictionary<string, Waddamburo.Game.Scores.TaikoCrown>? _crowns;
    private OverlayPage? _page;

    public bool IsOpen { get; private set; }

    // The last searches, beside the settings file (memory only without one).
    private string? historyPath => shell.Options.ArcadePath is { } settings
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings))!, "search_history.txt") : null;

    /// <summary>Takes the tick's input while open (or as Tab opens it); false leaves it to the scene.</summary>
    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
        {
            // Not during a scene switch (entry -> Song Select): the reload's restart needs an active scene.
            if (!GameActions.Down(keys, GameAction.Search) || shell.Active.Id != FlowScenes.SongSelect
                || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active
                || shell.Hosts.SongSelect is null || shell.PlayRequests.Pending is not null)
                return false;
            open();
            return true;
        }
        _tick++;
        _menu.Tick(SdlKeyboardSnapshot.Empty, SdlKeyboardSnapshot.Empty, false); // the selection's blink only
        if (escape || GameActions.Down(keys, GameAction.Search))
        {
            Close();
            return true;
        }
        var typed = shell.Application.TakeTypedText();
        if (typed.Length > 0)
            edit(_query[.._caret] + typed + _query[_caret..], _caret + typed.Length);
        // Whole characters (surrogate pairs, combining marks) at a time.
        var starts = StringInfo.ParseCombiningCharacters(_query);
        int before() => starts.LastOrDefault(start => start < _caret);
        int after() => starts.FirstOrDefault(start => start > _caret, _query.Length);
        if (keys.IsDown(SdlKeyboardKey.Backspace) && _caret > 0)
            edit(_query[..before()] + _query[_caret..], before());
        else if (keys.IsDown(SdlKeyboardKey.Delete) && _caret < _query.Length)
            edit(_query[.._caret] + _query[after()..], _caret);
        else if (keys.IsDown(SdlKeyboardKey.Left))
            (_caret, _tick) = (before(), 0);
        else if (keys.IsDown(SdlKeyboardKey.Right))
            (_caret, _tick) = (after(), 0);
        else if (keys.IsDown(SdlKeyboardKey.Up) || keys.IsDown(SdlKeyboardKey.Down))
            recall(keys.IsDown(SdlKeyboardKey.Up) ? -1 : 1);
        else if (keys.IsDown(SdlKeyboardKey.Enter))
        {
            _openWhenDone = true;
            sound(don: true);
        }
        if (_openWhenDone && _result is var done && done.Query == _query)
        {
            _openWhenDone = false;
            if (done.Songs.Length > 0)
                openResults(done);
        }
        return true;
    }

    private void open()
    {
        IsOpen = true;
        (_query, _caret, _tick, _openWhenDone) = ("", 0, 0, false);
        _result = new("", []);
        _crowns = shell.Hosts.Crowns?.Invoke(shell.Hosts.PlayerSide);
        _history = loadHistory();
        _page ??= new(static () => Strings.T("search.title"), rows, Footer: legend);
        _menu.Open(_page, home: true);
        shell.Application.TakeTypedText(); // the Tab that opened it isn't text
        shell.Application.StartTextInput();
    }

    // The field, the count, then the last searches; the field is row 0 of the selectable ones, the searches 1 on.
    private OverlayRow[] rows() =>
    [
        new(OverlayMenu.upper(Strings.T("search.field")), 400, field, 560, Enter: static () => { }),
        new(OverlayMenu.upper(Strings.T("search.results")), 400, count, 560, Cyan: true),
        new(OverlayMenu.upper(Strings.T(_history.Count == 0 ? "search.no_recent" : "search.recent")), 400, Gap: true, Cyan: true),
        .. _history.Select(entry => new OverlayRow(shorten(entry), 430, Enter: static () => { })),
    ];

    // The text with its cursor, which blinks (and shows at once after a key).
    private string field()
    {
        var shown = _query.Replace('{', '(').Replace('}', ')'); // braces colour an overlay value
        var caret = _tick % 36 < 24 ? "|" : " ";
        return shown[.._caret] + caret + shown[_caret..];
    }

    private string count()
    {
        if (_query.Trim().Length == 0) return Strings.T("search.type");
        var result = _result;
        return result.Query != _query ? Strings.T("search.searching") : Strings.T("search.count", result.Songs.Length);
    }

    private static string shorten(string text) => text.Length <= ShownCharacters ? text : text[..(ShownCharacters - 1)] + "…";

    // The keys and the filters, at the bottom of the page.
    private static (string Text, RenderColor Colour)[] legend() =>
    [
        (Strings.T("search.legend.keys"), OverlayMenu.White),
        (Strings.T("search.legend.filters"), OverlayMenu.Cyan),
        (Strings.T("search.legend.crowns"), OverlayMenu.Cyan),
    ];

    private void edit(string query, int caret)
    {
        (_caret, _tick) = (Math.Clamp(caret, 0, query.Length), 0);
        _menu.Selection = 0; // typing is in the field
        search(query);
    }

    // ↑/↓: through the field and the last searches; a search reached fills the field (Enter opens it).
    private void recall(int step)
    {
        var selection = Math.Clamp(_menu.Selection + step, 0, _history.Count);
        if (selection == _menu.Selection)
            return;
        sound(don: false);
        _menu.Selection = selection;
        if (selection > 0)
        {
            (_caret, _tick) = (_history[selection - 1].Length, 0);
            search(_history[selection - 1]);
        }
    }

    private void openResults(SearchResult done)
    {
        var results = new SongSearchResults(done.Query.Trim(), done.Songs);
        Console.WriteLine($"Search '{results.Query}': {results.Songs.Count} songs.");
        remember(results.Query);
        Close();
        shell.Reload.Start(() => shell.Hosts.Search = results);
    }

    // The search goes first in the history, once (a search picked again moves up); the last five are kept.
    private void remember(string query)
    {
        _history = [query, .. _history.Where(entry => !string.Equals(entry, query, StringComparison.OrdinalIgnoreCase)).Take(HistoryLength - 1)];
        if (historyPath is not { } path)
            return;
        try
        {
            File.WriteAllLines(path, _history);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning SEARCH_HISTORY: not saved ({exception.Message}).");
        }
    }

    private List<string> loadHistory()
    {
        try
        {
            return historyPath is { } path && File.Exists(path)
                ? [.. File.ReadAllLines(path).Where(static line => line.Trim().Length > 0).Take(HistoryLength)] : _history;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return _history;
        }
    }

    private void sound(bool don) => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false);

    private sealed record SearchResult(string Query, SongKey[] Songs);

    // A scan of every song per keystroke, off the game thread (a big library took long enough to drop frames); a newer
    // keystroke cancels the one before.
    private void search(string query)
    {
        _query = query;
        var generation = Interlocked.Increment(ref _generation);
        bool stale() => Volatile.Read(ref _generation) != generation;
        var crowns = _crowns;
        // Waiwai's folder lists only songs it has a composition for (SongSelectCatalogView): the count says the same.
        var waiwai = shell.Hosts.Waiwai;
        _ = Task.Run(() =>
        {
            try
            {
                var songs = catalog.Songs.Values.Where(song =>
                    {
                        if (stale()) throw new OperationCanceledException();
                        return (!waiwai || song.WaiwaiComposition is not null) && SongBrowse.Matches(song, query, crowns is null ? null : chart => crowns.GetValueOrDefault(chart.Key.ToString()));
                    })
                    .OrderBy(static song => song.Title.English ?? song.Title.Primary, StringComparer.OrdinalIgnoreCase)
                    .Take(SongBrowse.MaxFolderSongs)
                    .Select(static song => song.Key).ToArray();
                if (!stale())
                    _result = new(query, songs);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Closes the field (also when the service menu resets the game).</summary>
    public void Close()
    {
        Interlocked.Increment(ref _generation);
        _openWhenDone = false;
        IsOpen = false;
        _menu.Close();
        shell.Application.StopTextInput();
    }

    public IEnumerable<RenderQuad> Quads() => IsOpen ? _menu.Quads() : [];

    public void Dispose() => _menu.Dispose();
}
