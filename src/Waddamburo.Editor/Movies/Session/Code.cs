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

    private IReadOnlyList<ProviderMistake> _providerMistakes = [];
    private long _providerChecked;

    // Everything wrong or doubtful with the scripts, each with its file where one is known (click: the line in VS Code).
    private int issues(Waddamburo.Lumen.Runtime.LumenPlayer player, bool draw)
    {
        if (_document is { ReadOnly: false } && System.Diagnostics.Stopwatch.GetElapsedTime(_providerChecked).TotalSeconds > 1)
        {
            _providerMistakes = _document.ProviderMistakes();
            _providerChecked = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var red = new System.Numerics.Vector4(1, .42f, .38f, 1);
        var count = 0;
        void row(string id, System.Numerics.Vector4 colour, string text, string? file = null, int line = 1, int column = 1)
        {
            count++;
            if (!draw) return;
            ImGui.PushStyleColor(ImGuiCol.Text, colour);
            if (ImGui.Selectable($"{text}##{id}") && file is not null) openCode(file, line, column);
            ImGui.PopStyleColor();
            if (file is not null && ImGui.IsItemHovered()) ImGui.SetTooltip("Click to open the line in VS Code.");
        }
        for (var index = 0; index < ScriptErrors.Count; index++)
            row($"e{index}", red, ScriptErrors[index].Message, ScriptErrors[index].File, ScriptErrors[index].Line, ScriptErrors[index].Column);
        foreach (var mistake in _providerMistakes)
            row($"p{mistake.File}{mistake.Line}{mistake.Column}", red,
                $"{mistake.File}:{mistake.Line}: \"{mistake.Name}\" is not a name the engine provides. A typing mistake?", mistake.File, mistake.Line, mistake.Column);
        // Asked while running but not found in a file's text: a name put together by the script.
        foreach (var unknown in player.ProviderUnknownNames.Where(name => _providerMistakes.All(mistake => mistake.Name != name)))
            row("u" + unknown, red, $"\"{unknown}\" is not a name the engine provides (asked while the movie ran).");
        foreach (var diagnostic in player.Diagnostics)
            row($"d{diagnostic.Code}{diagnostic.CharacterId}{diagnostic.Frame}{diagnostic.Message}", new(1, .78f, .35f, 1), diagnostic.Message);
        if (draw && count == 0)
            ImGui.TextDisabled("Nothing wrong found.");
        return count;
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
                : $"runs in sprite \"{uses.Key.Sprite}\" at frame{(uses.Count() > 1 ? "s" : "")} {string.Join(", ", uses.Select(static use => use.Frame).OrderBy(static frame => int.TryParse(frame, out var number) ? number : int.MaxValue))}")));
            ImGui.PopID();
        }
    }
}
