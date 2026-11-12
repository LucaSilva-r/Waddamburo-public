using System.Globalization;
using ImGuiNET;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private readonly List<string> _log = [];
    private readonly Dictionary<string, string[]> _arguments = new(StringComparer.Ordinal);
    private void host(EditorPanel rect, LumenPlayer player)
    {
        {
            var callbacks = player.Callbacks;
            ImGui.BeginChild("##callbacks", new(rect.Width * .62f, 0));
            provider(player);
            ImGui.SeparatorText("Callbacks the game calls");
            if (callbacks.IsEmpty)
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "This movie registers no callbacks.");
            foreach (var callback in callbacks)
            {
                if (!_arguments.TryGetValue(callback.Name, out var values) || values.Length != callback.Parameters.Length)
                    _arguments[callback.Name] = values = [.. callback.Parameters.Select(static _ => "")];
                if (ImGui.Button($"{callback.Name}##call"))
                {
                    var arguments = values.Select(parse).ToArray();
                    var text = player.TryInvokeCallback(callback.Name, arguments, out var result)
                        ? $"{callback.Name}({string.Join(", ", values)}) -> {show(result)}"
                        : $"{callback.Name}: the call failed";
                    _log.Insert(0, text);
                    if (_log.Count > 100)
                        _log.RemoveAt(_log.Count - 1);
                }
                for (var index = 0; index < values.Length; index++)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputTextWithHint($"##{callback.Name}{index}", callback.Parameters[index], ref values[index], 128);
                }
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##log");
            if (_document is { ReadOnly: false })
            {
                ImGui.SeparatorText("Script files");
                code();
            }
            ImGui.SeparatorText("Messages");
            foreach (var diagnostic in player.Diagnostics)
                ImGui.TextColored(new(1, .78f, .35f, 1), diagnostic.Message);
            ImGui.TextColored(new(.52f, .56f, .61f, 1), "Values typed on the left: numbers, true / false, null, anything else is text.");
            foreach (var line in _log)
                ImGui.TextUnformatted(line);
            ImGui.EndChild();
        }
    }

    // What the engine provides (Wd.get, Wd.on), from the engine's own list: every name, what this movie uses of it, and
    // a value to try. A value typed here is what the movie reads until it is cleared; it survives the movie's restarts.
    private readonly Dictionary<string, string> _wdTyped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wdSet = new(StringComparer.Ordinal);
    private bool _wdAll;

    private void provider(LumenPlayer player)
    {
        ImGui.SeparatorText("Game data (Wd)");
        var dim = new System.Numerics.Vector4(.52f, .56f, .61f, 1);
        foreach (var unknown in player.ProviderUnknownNames)
            ImGui.TextColored(new(1, .42f, .38f, 1), $"\"{unknown}\" is not a name the engine provides. A typing mistake?");
        var used = player.ProviderNames;
        if (used.Count == 0 && player.ProviderUnknownNames.Count == 0)
            ImGui.TextColored(dim, "This movie asks the engine for nothing yet. In a script: Wd.on(\"play.score\", function(score) { ... });");
        if (ImGui.SmallButton("Fill with sample values"))
            foreach (var name in WdNames.All.Where(name => name.Kind == WdKind.State && (_wdAll || used.Contains(name.Name))))
                _wdSet[name.Name] = _wdTyped[name.Name] = name.Sample;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear all"))
            _wdSet.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Show every name", ref _wdAll);
        // Kept up every frame: a movie restarted by an edit reads the same values again. Publish ignores no change.
        foreach (var name in WdNames.All)
            if (name.Kind == WdKind.State)
                player.Publish(name.Name, _wdSet.TryGetValue(name.Name, out var set) ? parse(set) : LumenHostValue.Undefined);
        foreach (var name in WdNames.All)
        {
            if (!_wdAll && !used.Contains(name.Name))
                continue;
            var typed = _wdTyped.GetValueOrDefault(name.Name, "");
            ImGui.PushID(name.Name);
            ImGui.SetNextItemWidth(150);
            var entered = ImGui.InputTextWithHint("##value", name.Kind == WdKind.Event ? name.Type : name.Sample, ref typed, 128,
                ImGuiInputTextFlags.EnterReturnsTrue);
            _wdTyped[name.Name] = typed;
            ImGui.SameLine();
            if (name.Kind == WdKind.State)
            {
                if (ImGui.SmallButton("Set") || entered)
                    _wdSet[name.Name] = typed;
                ImGui.SameLine();
                if (ImGui.SmallButton("None"))
                    _wdSet.Remove(name.Name);
            }
            else if (ImGui.SmallButton("Send") || entered)
            {
                player.Emit(name.Name, [.. (typed.Length == 0 ? name.Sample : typed).Split(',').Select(parse)]);
                _log.Insert(0, $"Wd {name.Name}({(typed.Length == 0 ? name.Sample : typed)})");
            }
            ImGui.SameLine();
            ImGui.TextColored(used.Contains(name.Name) ? new(1, 1, 1, 1) : dim, name.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{name.Description}\n{(name.Kind == WdKind.Event ? "Event" : "State")}: {name.Type}. Has a value during: {name.Scope}.");
            if (name.Kind == WdKind.State)
            {
                ImGui.SameLine();
                ImGui.TextColored(dim, "= " + show(player.Provided(name.Name)));
            }
            ImGui.PopID();
        }
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
