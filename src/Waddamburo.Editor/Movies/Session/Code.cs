using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private void openCode(string file, int line = 1, int column = 1)
    {
        try
        {
            _document!.OpenInCode(file, line, column);
            _notice = null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            _notice = $"Could not open VS Code. Make sure the 'code' command is installed: {error.Message}";
        }
    }

    private void code()
    {
        // One row per file, with where it runs: a file used on many frames (a stop) is one row, not one per frame.
        foreach (var file in _document!.Scripts().GroupBy(static script => script.File))
        {
            ImGui.PushID(file.Key);
            if (ImGui.SmallButton("Open in VS Code")) openCode(file.Key);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Saving recompiles and reloads the movie, also while paused. Script state restarts on reload.");
            ImGui.SameLine();
            ImGui.TextUnformatted(file.Key);
            ImGui.SameLine();
            ImGui.TextDisabled(string.Join("; ", file.GroupBy(static use => (use.IsClass, use.Sprite)).Select(static uses =>
                uses.Key.IsClass ? "a class"
                : $"runs in sprite \"{uses.Key.Sprite}\" at frame{(uses.Count() > 1 ? "s" : "")} {string.Join(", ", uses.Select(static use => use.Frame).Order())}")));
            ImGui.PopID();
        }
        if (ScriptErrors.Count != 0)
        {
            ImGui.TextColored(new(1, .42f, .38f, 1), "Compilation errors (click to open the source):");
            for (var index = 0; index < ScriptErrors.Count; index++)
            {
                var error = ScriptErrors[index];
                ImGui.PushID(index);
                if (ImGui.Selectable(error.Message)) openCode(error.File, error.Line, error.Column);
                ImGui.PopID();
            }
        }
    }
}
