using System.Globalization;
using ImGuiNET;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private readonly List<string> _log = [];
    private readonly Dictionary<string, string[]> _arguments = new(StringComparer.Ordinal);
    // Three panes side by side, each with its own heading, padding and scroll: what the movie asks of the engine, what
    // the game calls on it, and the files with what came of it.
    private void host(EditorPanel rect, LumenPlayer player)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new System.Numerics.Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new System.Numerics.Vector2(6, 3));
        var width = ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X;
        var dim = new System.Numerics.Vector4(.52f, .56f, .61f, 1);

        ImGui.BeginChild("##data", new(width * .36f, 0), ImGuiChildFlags.Borders);
        provider(player);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##callbacks", new(width * .30f, 0), ImGuiChildFlags.Borders);
        ImGui.TextUnformatted("Callbacks the game calls");
        ImGui.Separator();
        var callbacks = player.Callbacks;
        if (callbacks.IsEmpty)
            ImGui.TextColored(dim, "This movie registers no callbacks.");
        else if (ImGui.BeginTable("##calls", 2, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("call");
            ImGui.TableSetupColumn("arguments", ImGuiTableColumnFlags.WidthStretch);
            var button = callbacks.Max(static callback => ImGui.CalcTextSize(callback.Name).X) + 2 * ImGui.GetStyle().FramePadding.X;
            foreach (var callback in callbacks)
            {
                if (!_arguments.TryGetValue(callback.Name, out var values) || values.Length != callback.Parameters.Length)
                    _arguments[callback.Name] = values = [.. callback.Parameters.Select(static _ => "")];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Button($"{callback.Name}##call", new(button, 0)))
                {
                    var arguments = values.Select(parse).ToArray();
                    log(player.TryInvokeCallback(callback.Name, arguments, out var result)
                        ? $"{callback.Name}({string.Join(", ", values)}) -> {show(result)}"
                        : $"{callback.Name}: the call failed");
                }
                ImGui.TableNextColumn();
                var each = values.Length == 0 ? 0 : (ImGui.GetContentRegionAvail().X - (values.Length - 1) * ImGui.GetStyle().ItemSpacing.X) / values.Length;
                for (var index = 0; index < values.Length; index++)
                {
                    if (index > 0) ImGui.SameLine();
                    ImGui.SetNextItemWidth(each);
                    ImGui.InputTextWithHint($"##{callback.Name}{index}", callback.Parameters[index], ref values[index], 128);
                }
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##log", default, ImGuiChildFlags.Borders);
        if (_document is { ReadOnly: false })
        {
            ImGui.TextUnformatted("Script files");
            ImGui.Separator();
            code();
            ImGui.Spacing();
        }
        if (ImGui.BeginTabBar("##messages"))
        {
            var found = issues(player, draw: false);
            // The label changes with the count; the id after ### does not, so the tab stays selected.
            if (ImGui.BeginTabItem(found == 0 ? "Issues###issues" : $"Issues ({found})###issues"))
            {
                issues(player, draw: true);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Log"))
            {
                if (_log.Count == 0)
                    ImGui.TextColored(dim, "What you set, send or call shows here, newest first.");
                foreach (var line in _log)
                    ImGui.TextUnformatted(line);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(3);
    }

    private void log(string text)
    {
        _log.Insert(0, text);
        if (_log.Count > 100)
            _log.RemoveAt(_log.Count - 1);
    }

    // What the engine provides (Wd.get, Wd.on), from the engine's own list: every name, what this movie uses of it, and
    // a value to try. A value typed here is what the movie reads until it is cleared; it survives the movie's restarts.
    private readonly Dictionary<string, string> _wdTyped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wdSet = new(StringComparer.Ordinal);
    private bool _wdAll;

    private void provider(LumenPlayer player)
    {
        var dim = new System.Numerics.Vector4(.52f, .56f, .61f, 1);
        var used = player.ProviderNames;
        ImGui.TextUnformatted("Game data (Wd)");
        ImGui.SameLine();
        ImGui.TextColored(dim, "numbers, true / false, anything else is text");
        ImGui.Separator();
        if (ImGui.SmallButton("Fill with sample values"))
            foreach (var name in WdNames.All.Where(name => name.Kind == WdKind.State && (_wdAll || used.Contains(name.Name))))
                _wdSet[name.Name] = _wdTyped[name.Name] = name.Sample;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear all"))
            _wdSet.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Show every name", ref _wdAll);
        // Diagnostic: WADDAMBURO_EDITOR_EMIT="drum.hit:left,don" sends that event every 20 frames, for a screenshot run.
        if (ImGui.GetFrameCount() % 20 == 0 && Environment.GetEnvironmentVariable("WADDAMBURO_EDITOR_EMIT") is { } repeated
            && repeated.Split(':') is [var repeatedName, var repeatedArguments])
            player.Emit(repeatedName, [.. repeatedArguments.Split(',').Select(parse)]);
        // Kept up every frame: a movie restarted by an edit reads the same values again. Publish ignores no change.
        foreach (var name in WdNames.All)
            if (name.Kind == WdKind.State)
                player.Publish(name.Name, _wdSet.TryGetValue(name.Name, out var set) ? parse(set) : LumenHostValue.Undefined);
        if (used.Count == 0)
            ImGui.TextWrapped("This movie has asked the engine for nothing yet. In a script: Wd.on(\"play.score\", function(score) { ... });");
        if ((!_wdAll && used.Count == 0) || !ImGui.BeginTable("##names", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            return;
        ImGui.TableSetupColumn("name");
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("do");
        ImGui.TableSetupColumn("now");
        foreach (var name in WdNames.All)
        {
            if (!_wdAll && !used.Contains(name.Name))
                continue;
            var typed = _wdTyped.GetValueOrDefault(name.Name, "");
            ImGui.PushID(name.Name);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(used.Contains(name.Name) ? new(1, 1, 1, 1) : dim, name.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{name.Description}\n{(name.Kind == WdKind.Event ? "Event" : "State")}: {name.Type}. Has a value during: {name.Scope}.");
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var entered = ImGui.InputTextWithHint("##value", name.Kind == WdKind.Event ? name.Type : name.Sample, ref typed, 128,
                ImGuiInputTextFlags.EnterReturnsTrue);
            _wdTyped[name.Name] = typed;
            ImGui.TableNextColumn();
            if (name.Kind == WdKind.State)
            {
                if (ImGui.Button("Set") || entered)
                {
                    _wdSet[name.Name] = typed;
                    log($"Wd {name.Name} = {typed}");
                }
                ImGui.SameLine();
                if (ImGui.Button("None") && _wdSet.Remove(name.Name))
                    log($"Wd {name.Name} = (no value)");
            }
            else if (name.Kind == WdKind.List)
            {
                // A list's place: "index, value" (the index alone: that place's sample value).
                if (ImGui.Button("Set at") || entered)
                {
                    var parts = (typed.Length == 0 ? "0" : typed).Split(',', 2);
                    if (int.TryParse(parts[0], out var index) && index >= 0)
                    {
                        player.PublishItem(name.Name, index, parse(parts.Length > 1 ? parts[1] : name.Sample));
                        log($"Wd {name.Name}[{index}] = {(parts.Length > 1 ? parts[1].Trim() : name.Sample)}");
                    }
                }
            }
            else if (ImGui.Button("Send") || entered)
            {
                player.Emit(name.Name, [.. (typed.Length == 0 ? name.Sample : typed).Split(',').Select(parse)]);
                log($"Wd {name.Name}({(typed.Length == 0 ? name.Sample : typed)})");
            }
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(dim, name.Kind == WdKind.Event ? "event" : name.Kind == WdKind.List ? "list: index, value"
                : player.Provided(name.Name) is { Kind: not LumenHostValueKind.Undefined } now ? show(now) : "no value");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static LumenHostValue parse(string text) => text.Trim() switch
    {
        "" or "undefined" => LumenHostValue.Undefined,
        "null" => LumenHostValue.Null,
        "true" => LumenHostValue.FromBoolean(true),
        "false" => LumenHostValue.FromBoolean(false),
        var value when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            => LumenHostValue.FromNumber(parsed),
        var value => LumenHostValue.FromString(value),
    };

    private static string show(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Number => number(value.AsNumber()),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        var kind => kind.ToString().ToLowerInvariant(),
    };
}
