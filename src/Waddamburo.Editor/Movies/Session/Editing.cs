using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    // What the last refused operation said (a name taken, an asset in use...).
    private string? _notice;

    /// <summary>Runs a document operation; one it refuses becomes the notice in the toolbar instead of an error.</summary>
    private bool attempt(Action operation)
    {
        try
        {
            edited(operation);
            _notice = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _notice = exception.Message;
            return false;
        }
    }

    // A change to the document reloads the movie: every clip goes back to the frame it was on, held there, so the
    // stage shows the same moment with the change in it. (What scripts had set is gone: the movie starts over.)
    private void edited(Action change)
    {
        var frames = _player().InspectDisplayList().Where(static clip => clip.Kind == LumenInstanceKind.Sprite)
            .Select(static clip => (clip.Path, clip.Frame, clip.Playing)).ToArray();
        change();
        var fresh = _player();
        foreach (var (path, frame, playing) in frames)
            fresh.InspectGoto(path, frame, playing);
        Paused = true;
    }

    // The layer of movie.json a clip on stage comes from: its parent's sprite, by depth and character.
    private (System.Text.Json.Nodes.JsonObject Layer, int Frame)? layerOf(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        var cut = clip.Path.LastIndexOf('/');
        var parent = clips.FirstOrDefault(each => each.Path == (cut < 0 ? "" : clip.Path[..cut]));
        if (_document is null || clip.Level == 0 || parent is null
            || _document.CharacterName(parent.CharacterId) is not { } sprite || _document.CharacterName(clip.CharacterId) is not { } character
            || _document.Layer(sprite, clip.Depth - LumenPlayer.TimelineDepthBase, character) is not { } layer)
            return null;
        return (layer, parent.Frame);
    }
}
