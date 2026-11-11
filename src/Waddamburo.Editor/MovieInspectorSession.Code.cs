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
        ImGui.TextWrapped("Open a script in VS Code. Saving recompiles and reloads the movie automatically, including while paused. Script state restarts on reload.");
        foreach (var script in _document!.Scripts())
        {
            ImGui.PushID($"{script.File}/{script.Sprite}/{script.Frame}");
            if (ImGui.SmallButton("Open in VS Code")) openCode(script.File);
            ImGui.SameLine();
            ImGui.TextUnformatted(script.File);
            ImGui.SameLine();
            ImGui.TextDisabled(script.IsClass ? "Class" : $"{script.Sprite}, frame {script.Frame}");
            ImGui.PopID();
        }
        if (ScriptErrors.Count == 0)
            ImGui.TextDisabled("No compilation errors.");
        else
        {
            ImGui.SeparatorText("Compilation errors — click to open the source");
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
