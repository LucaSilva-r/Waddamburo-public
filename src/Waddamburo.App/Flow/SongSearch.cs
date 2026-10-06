using System.Globalization;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// Song Select's search (after TaikoRecomp's browser): Tab opens a field over the screen, typed text
/// searches every library (titles, subtitles, artist, mapper; every word must match), Enter lists the
/// matches in a "Search: …" folder first and Song Select reloads on it (<see cref="SongSelectReload"/>),
/// Escape closes the field. While it is open the movie gets no input.
/// </summary>
internal sealed class SongSearch(GameShell shell, SongCatalogSnapshot catalog, OverlayPainter painter) : IInputOverlay
{
    private const int Width = 640, Height = 96;
    private string _query = "";
    private SongKey[] _matches = [];

    public bool IsOpen { get; private set; }

    /// <summary>Takes the tick's input while open (or as Tab opens it); false leaves it to the scene.</summary>
    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
        {
            if (!GameActions.Down(keys, GameAction.Search) || shell.Active.Id != FlowScenes.SongSelect
                || shell.Hosts.SongSelect is not { CourseSelectSong: null } || shell.PlayRequests.Pending is not null)
                return false;
            IsOpen = true;
            _query = "";
            _matches = [];
            shell.Application.StartTextInput();
            return true;
        }
        var typed = shell.Application.TakeTypedText();
        if (typed.Length > 0)
            search(_query + typed);
        if (keys.IsDown(SdlKeyboardKey.Backspace) && _query.Length > 0)
        {
            // A whole character (surrogate pairs, combining marks) at a time.
            var elements = StringInfo.ParseCombiningCharacters(_query);
            search(_query[..elements[^1]]);
        }
        if (escape)
            close();
        else if (keys.IsDown(SdlKeyboardKey.Enter) && _matches.Length > 0)
        {
            var results = new SongSearchResults(_query.Trim(), _matches);
            Console.WriteLine($"Search '{results.Query}': {results.Songs.Count} songs.");
            close();
            shell.Reload.Start(() => shell.Hosts.Search = results);
        }
        return true;
    }

    private void search(string query)
    {
        _query = query;
        // ponytail: a linear scan per keystroke; fine for tens of thousands of songs.
        _matches = [.. catalog.Songs.Values.Where(song => SongBrowse.Matches(song, query))
            .OrderBy(static song => song.Title.English ?? song.Title.Primary, StringComparer.OrdinalIgnoreCase)
            .Take(SongBrowse.MaxFolderSongs)
            .Select(static song => song.Key)];
    }

    private void close()
    {
        IsOpen = false;
        shell.Application.StopTextInput();
    }

    public IEnumerable<RenderQuad> Quads()
    {
        if (!IsOpen)
            return [];
        var text = $"Search: {_query}_   ({(_query.Trim().Length == 0 ? "type to search" : $"{_matches.Length} songs")})";
        float x = (OverlayPainter.StageWidth - Width) / 2, y = (OverlayPainter.StageHeight - Height) / 2;
        return
        [
            painter.Panel(x, y, Width, Height, (254, 205, 1), (0, 0, 0), radius: 18, outlineWidth: 4),
            painter.Text(text, OverlayPainter.StageWidth / 2, OverlayPainter.StageHeight / 2, Width - 48, Height - 36),
        ];
    }
}
