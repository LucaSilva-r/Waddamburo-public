using System.Diagnostics;
using Waddamburo.Editor;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Flow;

/// <summary>
/// The skin editor started from the game (F7, Waddamburo's frontend): a second process on the same skin folder, kept
/// in step both ways. The game plays the folder's packages and shows a saved change at once (<see cref="SkinDevReload"/>);
/// the editor is told which screen the game shows and everything the game's provider gives each movie on it
/// (<see cref="SkinLinkWire"/>), so its previews show the real song list, the real play.
/// </summary>
internal sealed class SkinEditorLink(GameShell shell, string root, Action<int, Waddamburo.Game.LumenMovieContent> swap) : IDisposable
{
    private readonly Dictionary<string, string> _edited = new(StringComparer.Ordinal); // folder -> movie.json being edited
    private readonly SkinLinkServer _server = new();
    private LumenGameSceneInstance? _tapped;
    private Process? _editor;
    private bool _restarting;

    public string Root => root;

    /// <summary>Starts the editor (again, if it was closed); a running one is left as it is.</summary>
    public void Launch()
    {
        if (_editor is { HasExited: false })
            return;
        var executable = Environment.ProcessPath ?? "dotnet";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        // Run as `dotnet Waddamburo.App.dll`: the editor the same way.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SkinEditorLink).Assembly.Location);
        start.ArgumentList.Add($"--skin={root}");
        start.ArgumentList.Add($"--attach={_server.Port}");
        start.ArgumentList.Add("--no-crash-screen");
        _editor = Process.Start(start);
        Console.WriteLine($"Skin editor started on {root} (link port {_server.Port}).");
    }

    private string? _wanted;      // a screen the editor asked for, being reached
    private int _wantedSince;
    private int? _revealAt;       // a transition shown for the editor: when it opens again, then goes
    private bool _revealed;

    // What the movies were given since the last tick, in order, a state by its last value only (a song changes some
    // every tick, for ~30 movies). Sent once a tick.
    private readonly ProvidedBatch _batch = new();

    private void held(string movie, Waddamburo.Lumen.Runtime.LumenProvided provided)
    {
        if (_server.Connected) _batch.Add(movie, provided);
    }

    /// <summary>An editor session runs (from F7 until the editor closes): plays are not saved, the window may lose focus.</summary>
    public bool Editing => _editor is { HasExited: false };

    public void Tick()
    {
        foreach (var (movie, provided) in _batch.Take())
            _server.Send(SkinLinkWire.Provided(movie, provided));
        // The editor closed: the game starts again from the beginning, as it was before the session (nothing of it kept).
        if (_editor is { HasExited: true } && !_restarting)
        {
            _restarting = true;
            Console.WriteLine("Skin editor closed: starting the game again, clean.");
            shell.RequestRestart();
            return;
        }
        while (_server.TryTake(out var line))
        {
            if (SkinLinkWire.Command(line) is ("show", var asked))
                (_wanted, _wantedSince) = (asked, shell.Tick);
            else if (SkinLinkWire.TryPackage(line) is var (folder, json))
                _edited[folder] = json;
        }
        swapEdited();
        if (_wanted is { } wanted && shell.Tick - _wantedSince > 600)
        {
            _server.Send(SkinLinkWire.Notice($"The game could not get to {wanted}."));
            _wanted = null;
        }
        else if (_wanted is not null)
            reach();
        if (_revealAt is { } reveal && shell.Tick >= reveal)
        {
            if (!_revealed) { shell.Overlay.Reveal(); _revealed = true; }
            else if (shell.Tick >= reveal + 90) { shell.Overlay.Clear(); (_revealAt, _revealed) = (null, false); }
        }
        var joined = _server.TakeJoined();
        if (!joined && ReferenceEquals(_tapped, shell.Active))
            return;
        if (_tapped is not null)
            foreach (var layer in _tapped.Player.Layers)
                layer.Player.ProviderTap = null;
        _tapped = shell.Active;
        if (SkinScenes.Screens.FirstOrDefault(screen => screen.Value.Id == _tapped.Id).Key is { } key)
            _server.Send(SkinLinkWire.Screen(key));
        for (var index = 0; index < _tapped.Layers.Length && index < _tapped.Player.Layers.Length; index++)
        {
            var definition = _tapped.Layers[index].Definition;
            if (DirectoryLumenMovieContentSource.PackageFolder(root, definition.ArchiveId, definition.MovieId) is not { } folder)
                continue;
            var movie = Path.GetRelativePath(root, folder).Replace('\\', '/');
            var player = _tapped.Player.Layers[index].Player;
            // What it holds already (an editor joining late, a screen just entered), then each delivery.
            foreach (var provided in player.ProvidedNow())
                _server.Send(SkinLinkWire.Provided(movie, provided));
            player.ProviderTap = provided => held(movie, provided);
        }
    }

    // One step towards the screen the editor asked for (some take two: gameplay goes through Song Select). The screen
    // must be still (no switch, reload or fade under way); a song is left to its player, not stopped.
    private void reach()
    {
        var key = _wanted!;
        if (shell.Reload.Busy || shell.Scenes.FadePending || shell.Coordinator.Flow.State != Waddamburo.Game.Flow.GameFlowState.Active
            || shell.Overlay.IsShown && _revealAt is null)
            return;
        void done(string? notice = null)
        {
            _wanted = null;
            if (notice is not null) _server.Send(SkinLinkWire.Notice(notice));
        }
        if (shell.Scenes.OnLane && key != "gameplay")
        {
            done("The game is playing a song: leave it (pause > quit), then pick the screen again.");
            return;
        }
        var screens = shell.Screens;
        SceneDefinition? cover = key switch
        {
            "rainbow" => screens.Rainbow, "shutter" => screens.Shutter, "fade" => screens.Fade,
            "dan-cover" => screens.DanCover, "dan-exit-cover" => screens.DanExitCover, "dan-shutter" => screens.DanShutter, _ => null,
        };
        if (cover is not null)
        {
            // A transition plays over the screen: covers, holds a moment, opens again.
            shell.Overlay.Show(cover);
            shell.Overlay.Cover();
            _revealAt = shell.Tick + 90;
            done();
            return;
        }
        void songSelect()
        {
            shell.Players.Reset();
            shell.Players.Join(0);
            shell.Catalog.Replace(screens.SongSelect([0]));
            shell.Scenes.Show(FlowScenes.SongSelect);
        }
        // Already there: nothing to do (the editor following the game asks for the screen it is on).
        if (key != "gameplay" && SkinScenes.Screens.TryGetValue(key, out var screen) && screen.Id == shell.Active.Id)
        {
            done();
            return;
        }
        switch (key)
        {
            case "boot" or "tutorial" or "entry":
                var id = SkinScenes.Screens[key].Id;
                if (key == "entry") shell.Players.Reset();
                if (screens.Initial([]).FirstOrDefault(scene => scene.Id == id) is { } definition)
                    shell.Catalog.Replace(definition);
                shell.Scenes.Show(id);
                done();
                break;
            case "song-select":
                if (shell.Active.Id != FlowScenes.SongSelect) songSelect();
                done();
                break;
            case "dani-select":
                shell.Players.Reset();
                shell.Players.Join(0);
                done(shell.StartDanDojo());
                break;
            case "gameplay":
                if (shell.Scenes.OnLane)
                    done();
                else if (shell.Active.Id != FlowScenes.SongSelect)
                    songSelect();
                // Song Select settles first (its list), then plays its first song as its own request would.
                else if (shell.Hosts.SongSelect is not null && shell.Tick - _wantedSince > 120)
                    done(Diagnostics.FlowSmokeRun.FirstPlay(shell, null) is var (request, _, _) && shell.PlayRequests.TryRequestPlay(request)
                        ? null : "The game could not start a song (an empty library?).");
                break;
            default:
                done($"{key} shows after a play: play a song in the game to get there.");
                break;
        }
    }

    // Packages being edited (not saved) whose movie is on the screen: swapped in place, the screen going on. A song's
    // lane holds its movies: an edit to one waits for a save (then the screen reloads after the song).
    private void swapEdited()
    {
        if (_edited.Count == 0 || shell.Scenes.OnLane || shell.Reload.Busy || shell.Coordinator.Flow.State != GameFlowState.Active)
            return;
        var scene = shell.Active;
        for (var index = 0; index < scene.Layers.Length; index++)
        {
            var definition = scene.Layers[index].Definition;
            if (DirectoryLumenMovieContentSource.PackageFolder(root, definition.ArchiveId, definition.MovieId) is not { } directory
                || !_edited.TryGetValue(Path.GetRelativePath(root, directory).Replace('\\', '/'), out var json))
                continue;
            try
            {
                var errors = new List<Waddamburo.Formats.Package.ActionScriptException>();
                var content = Waddamburo.Game.LumenMovieContent.LoadPackage(directory, Presentation.GameMovieContent.DecodeImage, scriptErrors: errors, movieJson: json);
                swap(index, content);
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or IOException or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Skin editor: {definition.MovieId} not swapped: {error.Message}");
            }
        }
        _edited.Clear();
    }

    public void Dispose() => _server.Dispose();
}
