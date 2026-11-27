using System.Diagnostics;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>
/// The home page's Problems tab: <see cref="SkinLint"/> over the skin as saved, checked again in the background a moment
/// after a save (or Check again). A line opens its place: a script at the line in VS Code, a package in the
/// component editor, scenes.json or a scenario file in VS Code.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private IReadOnlyList<SkinLint.Problem> _problems = [];
    private Task<IReadOnlyList<SkinLint.Problem>>? _checking;
    private int _checkedVersion = int.MinValue;
    private long _checkedAt;
    private bool _showWarnings = true;

    private int problemCount(SkinLint.Level level) => _problems.Count(problem => problem.Level == level);

    // Checked again when the skin changed on disk (a save), at most every two seconds; the last result stays meanwhile.
    private void pollProblems(bool now = false)
    {
        if (_checking is { IsCompleted: true } done)
        {
            _problems = done.IsCompletedSuccessfully ? done.Result : [new(SkinLint.Level.Error, "", 0, 0, $"The check failed: {done.Exception?.InnerException?.Message}")];
            _checking = null;
        }
        var version = HashCode.Combine(Skin!.Version, Scenes?.IsDirty);
        if (_checking is not null || !now && (version == _checkedVersion || Stopwatch.GetElapsedTime(_checkedAt).TotalSeconds < 2))
            return;
        _checkedVersion = version;
        _checkedAt = Stopwatch.GetTimestamp();
        var root = Skin.Root;
        _checking = Task.Run(() => SkinLint.Check(root));
    }

    private void problemsTab()
    {
        if (ImGui.SmallButton("Check again")) pollProblems(now: true);
        ImGui.SameLine();
        ImGui.Checkbox("Warnings", ref _showWarnings);
        ImGui.SameLine();
        ImGui.TextDisabled(_checking is not null ? "checking..." : $"{problemCount(SkinLint.Level.Error)} error(s), {problemCount(SkinLint.Level.Warning)} warning(s): the skin as saved. Click one to go there.");
        ImGui.BeginChild("##problems");
        if (_problems.Count == 0 && _checking is null)
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Good), "No problems.");
        if (ImGui.BeginTable("##problem list", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("level");
            ImGui.TableSetupColumn("where");
            ImGui.TableSetupColumn("what", ImGuiTableColumnFlags.WidthStretch);
            var index = 0;
            foreach (var problem in _problems)
            {
                if (problem.Level == SkinLint.Level.Warning && !_showWarnings) continue;
                ImGui.PushID(index++);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(problem.Level == SkinLint.Level.Error ? Bad : color(.95f, .75f, .3f)),
                    problem.Level == SkinLint.Level.Error ? "error" : "warning");
                ImGui.TableNextColumn();
                var where = problem.File + (problem.Line > 0 ? $":{problem.Line}" : "");
                if (ImGui.Selectable(where, false, ImGuiSelectableFlags.SpanAllColumns))
                    openProblem(problem);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(problem.Text);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private void openProblem(SkinLint.Problem problem)
    {
        var path = Path.Combine(Skin!.Root, problem.File);
        if (problem.File.EndsWith(".as", StringComparison.Ordinal) || problem.File.EndsWith(".json", StringComparison.Ordinal)
            && !problem.File.EndsWith(Formats.Package.MoviePackage.FileName, StringComparison.Ordinal))
            openInCode(path, problem.Line);
        else if (problem.Package is { } package && Skin.Exists(package))
            openPackage(package);
        else if (File.Exists(path))
            openInCode(path, problem.Line);
    }

    private void exportSkin()
    {
        try
        {
            _notice = $"Exported: {SkinLint.Export(Skin!.Root)}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _notice = $"Could not export: {error.Message}";
        }
    }

    // The MCP server entry (Waddamburo.App --mcp=<skin>) for an assistant's config (Claude Desktop's
    // claude_desktop_config.json, a project's .mcp.json, VS Code's mcp.json "servers"), on the clipboard.
    private void copyMcpSetup()
    {
        var command = Environment.ProcessPath ?? "dotnet";
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            args.Add(dll);
        args.Add($"--mcp={Skin!.Root}");
        var entry = new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = command,
            ["args"] = new System.Text.Json.Nodes.JsonArray([.. args.Select(static arg => (System.Text.Json.Nodes.JsonNode?)arg)]),
        };
        var config = new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = new System.Text.Json.Nodes.JsonObject { ["waddamburo-skin"] = entry } };
        ImGui.SetClipboardText(config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _notice = "Copied: paste it into your assistant's MCP settings (Claude Desktop: Settings > Developer > Edit Config).";
    }
}
