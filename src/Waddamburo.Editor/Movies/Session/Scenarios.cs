using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>
/// Preview scenarios (<see cref="PreviewScenario"/>): per screen, the data its previews play with, chosen in the scene
/// view and the live window. The files are written in the user's editor (New makes one and opens it); a saved file is
/// read again within half a second.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private const string ExampleData = "Example data";
    private readonly Dictionary<string, (DateTime Stamp, long Checked, IReadOnlyList<PreviewScenario> List, IReadOnlyList<string> Problems)> _scenarios = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _scenarioChosen = new(StringComparer.Ordinal);
    private int _scenarioVersion;

    private (IReadOnlyList<PreviewScenario> List, IReadOnlyList<string> Problems) scenarios(string key)
    {
        var now = Stopwatch.GetTimestamp();
        if (_scenarios.TryGetValue(key, out var known) && Stopwatch.GetElapsedTime(known.Checked, now).TotalSeconds < .5)
            return (known.List, known.Problems);
        var path = PreviewScenario.FilePath(Skin!.Root, key);
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_scenarios.TryGetValue(key, out known) && known.Stamp == stamp)
        {
            _scenarios[key] = known with { Checked = now };
            return (known.List, known.Problems);
        }
        var (list, problems) = PreviewScenario.Load(Skin.Root, key);
        _scenarios[key] = (stamp, now, list, problems);
        _scenarioVersion++;
        return (list, problems);
    }

    // The screen's chosen scenario; null: the editor's example data alone.
    private PreviewScenario? scenario(string key) =>
        _scenarioChosen.TryGetValue(key, out var name) ? scenarios(key).List.FirstOrDefault(each => each.Name == name) : null;

    private void scenarioPicker(string key)
    {
        var (list, problems) = scenarios(key);
        // Following a game showing this screen, "no scenario" is the game's own data.
        var unchosen = Skin.Live is { Connected: true } live && live.Screen == key ? "Live from the game" : ExampleData;
        var chosen = scenario(key)?.Name ?? unchosen;
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("##scenario", chosen))
        {
            if (ImGui.Selectable(unchosen, chosen == unchosen)) _scenarioChosen.Remove(key);
            foreach (var each in list)
                if (ImGui.Selectable(each.Name, each.Name == chosen)) _scenarioChosen[key] = each.Name;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"The data this screen's preview plays with.\nScenarios: {Path.Combine(PreviewScenario.Folder, key + ".json")} in the skin.");
        ImGui.SameLine();
        if (ImGui.SmallButton("New")) newScenario(key);
        ImGui.SameLine();
        ImGui.BeginDisabled(!File.Exists(PreviewScenario.FilePath(Skin!.Root, key)));
        if (ImGui.SmallButton("Edit")) openInCode(PreviewScenario.FilePath(Skin.Root, key), lineOf(key, chosen));
        ImGui.EndDisabled();
        if (problems.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Bad), $"{problems.Count} problem{(problems.Count == 1 ? "" : "s")}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join('\n', problems));
        }
    }

    // A new scenario at the end of the screen's file (made when missing), chosen and opened in the user's editor.
    private void newScenario(string key)
    {
        var path = PreviewScenario.FilePath(Skin!.Root, key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject()
                : new JsonObject { ["format"] = 1, ["scenarios"] = new JsonArray() };
            var list = root["scenarios"] as JsonArray ?? (JsonArray)(root["scenarios"] = new JsonArray());
            var names = list.Select(static each => each?["name"]?.GetValue<string>()).ToHashSet();
            var name = Enumerable.Range(1, 999).Select(static number => $"Scenario {number}").First(each => !names.Contains(each));
            list.Add(JsonNode.Parse(PreviewScenario.Skeleton(name)));
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            _scenarioChosen[key] = name;
            _scenarios.Remove(key);
            openInCode(path, lineOf(key, name));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _notice = $"Could not add a scenario: {error.Message}";
        }
    }

    private int lineOf(string key, string name)
    {
        var path = PreviewScenario.FilePath(Skin!.Root, key);
        if (!File.Exists(path)) return 1;
        var lines = File.ReadAllLines(path);
        var at = Array.FindIndex(lines, line => line.Contains(JsonSerializer.Serialize(name), StringComparison.Ordinal));
        return at < 0 ? 1 : at + 1;
    }

    private void openInCode(string path, int line)
    {
        try
        {
            var start = new ProcessStartInfo("code") { UseShellExecute = false };
            start.ArgumentList.Add("--reuse-window");
            start.ArgumentList.Add("--goto");
            start.ArgumentList.Add($"{path}:{Math.Max(1, line)}");
            using var process = Process.Start(start);
            _notice = null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _notice = $"Could not open VS Code ('code' on the PATH): {error.Message}";
        }
    }
}
