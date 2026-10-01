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
internal sealed class SongSearch(GameShell shell, SongCatalogSnapshot catalog, string fontPath) : IDisposable
{
    private const int Width = 640, Height = 96;
    private string _query = "";
    private SongKey[] _matches = [];
    private RenderTextureId? _texture;
    private (string Text, uint Scale)? _drawn;

    public bool IsOpen { get; private set; }


    /// <summary>Takes the tick's input while open (or as Tab opens it); false leaves it to the scene.</summary>
    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen)
        {
            if (!keys.IsDown(SdlKeyboardKey.Tab) || shell.Active.Id != FlowScenes.SongSelect
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
        var scale = (uint)Math.Clamp((shell.Application.GetPixelSize().Height + 719) / 720, 1, 4);
        if (_drawn != (text, scale))
        {
            if (_texture is { } old)
                shell.Application.ReleaseTexture(old);
            var canvas = new VectorCanvas(Width, Height, (int)scale, fontPath);
            canvas.RoundedRect(2, 2, Width - 4, Height - 4, 18, (254, 205, 1), outline: 4);
            canvas.Text(text, Width / 2f, Height / 2f, Width - 48, Height - 36);
            _texture = shell.Application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
            _drawn = (text, scale);
        }
        return [RenderQuad.FromRectangles(_texture!.Value,
            new RenderRectangle((1280f - Width) / 2 / 1280f, (720f - Height) / 2 / 720f, Width / 1280f, Height / 720f),
            RenderRectangle.Full, RenderColor.White, RenderColor.Transparent)];
    }

    public void Dispose()
    {
        if (_texture is { } texture)
            shell.Application.ReleaseTexture(texture);
    }
}
