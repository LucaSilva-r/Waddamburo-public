using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _scriptsEnabled = true;
    private string? _windowTitle;

    private float menuBar()
    {
        var height = ImGui.GetFrameHeight();
        if (!ImGui.BeginMainMenuBar()) return height;
        height = ImGui.GetWindowSize().Y;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open movie package...", "Ctrl+O", false, OpenMovie is not null)) pickMovie(false);
            if (ImGui.MenuItem("Open Lumen archive...", "", false, OpenMovie is not null)) pickMovie(true);
            ImGui.Separator();
            if (ImGui.MenuItem("Save", "Ctrl+S", false, _document is { ReadOnly: false })) saveDocument();
            ImGui.Separator();
            if (ImGui.MenuItem("Close")) requestOpenOrClose(true);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _document?.CanUndo == true)) edited(_document!.Undo);
            if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, _document?.CanRedo == true)) edited(_document!.Redo);
            ImGui.EndMenu();
        }
        ImGui.TextDisabled(_title + (_document?.IsDirty == true ? " *" : "") + (_gameData ? " (read-only)" : ""));
        if (_notice is not null || ScriptErrors.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new(1, .45f, .35f, 1), _notice ?? $"{ScriptErrors.Count} script error(s): {ScriptErrors[0].Message}");
        }
        ImGui.EndMainMenuBar();
        var title = $"Waddamburo — {_title}{(_document?.IsDirty == true ? " *" : "")}";
        if (_windowTitle != title) { _application.SetWindowTitle(title); _windowTitle = title; }
        return height;
    }

    private (string Path, int Frame, int Count) transportTarget()
    {
        var player = _player();
        if (_document is not null && timelineOf(player.InspectDisplayList()) is { } target)
            return (target.Clip.Path, target.Clip.Frame, target.Clip.FrameCount);
        return (_timelineContext.Path, 0, 0);
    }

    private void togglePlayback()
    {
        if (transportTarget().Count == 0) return;
        Paused = !Paused;
        if (!Paused) _player().InspectSetPlaying(transportTarget().Path, true);
    }

    private void seekTimeline(int frame)
    {
        var target = transportTarget();
        if (target.Count == 0) return;
        frame = Math.Clamp(frame, 0, Math.Max(0, target.Count - 1));
        Paused = true;
        if (target.Path.Length == 0) _player().Seek(frame);
        else _player().InspectGoto(target.Path, frame, play: false);
    }

    private void navigateTimeline(int direction, bool label)
    {
        var target = transportTarget();
        var frame = label ? MovieTransport.AdjacentLabel(target.Frame, direction, _player().InspectLabels(target.Path).Select(label => label.Frame))
            : Math.Clamp(target.Frame + direction, 0, Math.Max(0, target.Count - 1));
        if (frame is { } at) seekTimeline(at);
    }

    private void timelineTransport()
    {
        var target = transportTarget();
        void button(string text, string hint, Action action)
        {
            if (ImGui.Button(text)) action();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
            ImGui.SameLine();
        }
        button("|<", "Previous label (Ctrl+Left)", () => navigateTimeline(-1, true));
        button("<", "Previous frame (Left)", () => navigateTimeline(-1, false));
        button(Paused ? "Play" : "Pause", "Play / pause (Space)", togglePlayback);
        button(">", "Next frame (Right)", () => navigateTimeline(1, false));
        button(">|", "Next label (Ctrl+Right)", () => navigateTimeline(1, true));
        if (SetScriptsEnabled is not null)
        {
            if (ImGui.Checkbox("Run scripts", ref _scriptsEnabled))
            {
                SetScriptsEnabled(_scriptsEnabled);
                ShowSprite?.Invoke(_shown);
                Paused = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restarts the preview with scripts enabled or disabled.");
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X - 70));
        var frame = target.Frame;
        if (ImGui.SliderInt(target.Path.Length == 0 ? "Root frame" : "Frame", ref frame, 0, Math.Max(0, target.Count - 1))) seekTimeline(frame);
    }
}
