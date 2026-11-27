using System.Diagnostics;
using Waddamburo.Editor;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Flow;

/// <summary>
/// The skin editor started from the game (F7, Waddamburo's frontend): a second process on the same skin folder, kept
/// in step both ways. The game plays the folder's packages and shows a saved change at once (<see cref="SkinDevReload"/>);
/// the editor is told which screen the game shows and everything the game's provider gives each movie on it
/// (<see cref="SkinLinkWire"/>), so its previews show the real song list, the real play.
/// </summary>
internal sealed class SkinEditorLink(GameShell shell, string root) : IDisposable
{
    private readonly SkinLinkServer _server = new();
    private LumenGameSceneInstance? _tapped;
    private Process? _editor;

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

    public void Tick()
    {
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
            player.ProviderTap = provided => _server.Send(SkinLinkWire.Provided(movie, provided));
        }
    }

    public void Dispose() => _server.Dispose();
}
