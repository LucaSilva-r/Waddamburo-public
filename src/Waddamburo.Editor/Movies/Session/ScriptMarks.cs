using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>
/// Frame scripts on the timeline. Keys say how a frame looks; what happens next (stop, loop, jump, send a child to one
/// of its frames) is scripts, so each gets a mark on its frame saying which, and a child sent somewhere shows it on
/// its own row. A package's scripts are its .as files; a game movie's are read back from its bytecode, to look at.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private sealed record ScriptMark(string Key, int Frame, string Text, string Flow, (string Child, string To)[] Jumps);
    private (string Sprite, string Key)? _script; // The script shown in the inspector.
    private static readonly Regex OwnFlow = new(@"^\s*(?:this\.)?(stop|play|gotoAndPlay|gotoAndStop)\((.*)\);", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ChildFlow = new(@"^\s*(?:this\.|self\.)?(\w+)\.(gotoAndPlay|gotoAndStop|play|stop)\((.*)\);", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly uint ScriptColour = color(.55f, .85f, .6f), ScriptStop = color(.95f, .45f, .4f);

    private string scriptText(JsonNode? value)
    {
        var text = (string?)value ?? "";
        if (_document!.ReadOnly) return text;
        try
        {
            return System.Text.Encoding.UTF8.GetString(_document.ReadFile(text));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return $"// {text}: {exception.Message}";
        }
    }

    private List<ScriptMark> scriptMarks(JsonObject sprite)
    {
        var marks = new List<ScriptMark>();
        var labels = sprite["labels"] as JsonObject;
        foreach (var (key, value) in sprite["scripts"] as JsonObject ?? [])
        {
            // A script sits on a frame number or on one of the sprite's labels.
            if (!int.TryParse(key, out var frame) && (labels?[key] is not { } named || !int.TryParse(named.ToJsonString(), out frame)))
                continue;
            var text = scriptText(value);
            var flow = string.Join(", ", OwnFlow.Matches(text).Select(static match => match.Groups[1].Value switch
            {
                "stop" => "stop",
                "play" => "play",
                "gotoAndPlay" => "→ " + match.Groups[2].Value.Trim('"'),
                _ => $"→ {match.Groups[2].Value.Trim('"')}, stop",
            }).Distinct());
            marks.Add(new(key, frame, text, flow, [.. ChildFlow.Matches(text).Where(static match => match.Groups[1].Value is not ("this" or "self" or "_root"))
                .Select(static match => (match.Groups[1].Value, match.Groups[2].Value switch
                {
                    "gotoAndStop" => "→ " + match.Groups[3].Value.Trim('"'),
                    "gotoAndPlay" => "▶ " + match.Groups[3].Value.Trim('"'),
                    var other => other,
                }))]));
        }
        return marks;
    }

    // The scripts' own track, under the labels: a mark per script, saying what it does to this timeline.
    private float scriptTrack(string spriteName, List<ScriptMark> marks, ImDrawListPtr draw, float names, float top, Func<int, float> x, float step)
    {
        if (marks.Count == 0) return 0;
        draw.AddText(new(names + 24, top + 3), Muted, "scripts");
        foreach (var mark in marks.OrderBy(static mark => mark.Frame))
        {
            var at = new Vector2(x(mark.Frame) + step / 2, top + RowHeight / 2);
            var stops = mark.Flow.Contains("stop", StringComparison.Ordinal);
            var chosen = _script == (spriteName, mark.Key);
            var colour = chosen ? KeySelected : stops ? ScriptStop : ScriptColour;
            if (stops) draw.AddRectFilled(at - new Vector2(4), at + new Vector2(4), colour);
            else draw.AddTriangleFilled(at + new Vector2(-4, -5), at + new Vector2(5, 0), at + new Vector2(-4, 5), colour);
            var label = mark.Flow.Length > 0 ? mark.Flow : mark.Jumps.Length > 0 ? "" : "script";
            draw.AddText(at + new Vector2(8, -8), colour, label);
            ImGui.SetCursorScreenPos(at - new Vector2(7, RowHeight / 2));
            if (ImGui.InvisibleButton($"##script {mark.Key}", new(14 + ImGui.CalcTextSize(label).X + 8, RowHeight)))
            {
                selectClip(null);
                _selectedAsset = _selectedAssetId = null;
                _script = (spriteName, mark.Key);
            }
            if (ImGui.IsItemHovered())
            {
                var shown = mark.Text.Split('\n');
                ImGui.SetTooltip($"Script on frame {mark.Frame}. Click to read it in the Inspector.\n\n{string.Join('\n', shown.Take(14))}{(shown.Length > 14 ? "\n..." : "")}");
            }
        }
        return RowHeight;
    }

    // In the inspector: the selected script, to read. A package's opens in VS Code; a game movie's is only shown.
    private bool scriptInspector()
    {
        if (_script is not var (spriteName, key) || _document?.Root["sprites"]?[spriteName]?["scripts"]?[key] is not { } value)
        {
            _script = null;
            return false;
        }
        var text = scriptText(value);
        ImGui.TextUnformatted($"Script on {(int.TryParse(key, out _) ? "frame " : "label ")}{key} of {spriteName}");
        if (_document.ReadOnly)
        {
            ImGui.TextDisabled("Read back from the movie's compiled script, to see what it does.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Not the original source: names of local variables are lost (r1, r2 are registers), and code with\nbranches may be listed as instructions. Shown here only; nothing is written to disk.");
        }
        else if (ImGui.Button("Open in VS Code"))
            openCode((string?)value ?? "");
        ImGui.InputTextMultiline("##script text", ref text, (uint)text.Length + 1, new(-1, -1), ImGuiInputTextFlags.ReadOnly);
        return true;
    }
}
