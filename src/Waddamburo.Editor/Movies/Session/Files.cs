using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    public Action<string, string?>? OpenMovie { get; set; }
    public Func<string, IReadOnlyList<string>>? ArchiveMovies { get; set; }
    private Task<string?>? _moviePicker;
    private bool _pickingArchive, _showArchivePicker, _showUnsaved;
    private string? _openMoviePath;
    private string? _openArchiveMovie;
    private IReadOnlyList<string> _archiveMovieNames = [];
    private bool _closing, _unsavedDialogOpen;

    private bool saveDocument()
    {
        try { _document?.Save(); _notice = null; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _notice = "Could not save: " + error.Message; return false; }
    }

    private void pickMovie(bool archive)
    {
        if (_moviePicker is not null || OpenMovie is null) return;
        _pickingArchive = archive;
        try { _moviePicker = _application.PickFileAsync(false, archive ? "Lumen archive" : "Movie package", archive ? "ddp" : "json"); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { _notice = "Could not open the file picker: " + error.Message; }
    }

    private void pollMovieFiles()
    {
        if (_moviePicker is not { IsCompleted: true } picker) return;
        _moviePicker = null;
        try
        {
            if (picker.GetAwaiter().GetResult() is not { } path) return;
            _openMoviePath = path;
            _openArchiveMovie = null;
            if (_pickingArchive)
            {
                _archiveMovieNames = ArchiveMovies?.Invoke(path) ?? [];
                if (_archiveMovieNames.Count == 0) throw new InvalidOperationException("The archive contains no movies.");
                _showArchivePicker = true;
            }
            else requestOpenOrClose(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException)
        { _notice = "Could not open movie: " + error.Message; }
    }

    private bool confirmWindowClose()
    {
        if (_document?.IsDirty != true) return _previousClose?.Invoke() != false;
        requestOpenOrClose(true);
        return false;
    }

    private void requestOpenOrClose(bool close)
    {
        _closing = close;
        if (_document?.IsDirty == true) _showUnsaved = true;
        else finishOpenOrClose();
    }

    private void finishOpenOrClose()
    {
        try
        {
            if (_closing) _application.RequestQuit();
            else if (_openMoviePath is { } path) OpenMovie?.Invoke(path, _openArchiveMovie);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException)
        { _notice = "Could not open movie: " + error.Message; }
    }

    private void movieDialogs()
    {
        if (_showUnsaved) { ImGui.OpenPopup("Unsaved changes"); _showUnsaved = false; _unsavedDialogOpen = true; }
        if (ImGui.BeginPopupModal("Unsaved changes", ref _unsavedDialogOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Save changes before " + (_closing ? "closing?" : "opening another movie?"));
            if (ImGui.Button("Save") && saveDocument()) { ImGui.CloseCurrentPopup(); finishOpenOrClose(); }
            ImGui.SameLine();
            if (ImGui.Button("Discard")) { ImGui.CloseCurrentPopup(); finishOpenOrClose(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (_showArchivePicker) { ImGui.OpenPopup("Open Lumen movie"); _showArchivePicker = false; }
        if (ImGui.BeginPopup("Open Lumen movie"))
        {
            ImGui.BeginChild("##archive movies", new(420, 340));
            foreach (var name in _archiveMovieNames)
                if (ImGui.Selectable(name))
                {
                    _openArchiveMovie = name;
                    ImGui.CloseCurrentPopup();
                    requestOpenOrClose(false);
                    break;
                }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
    }
}
