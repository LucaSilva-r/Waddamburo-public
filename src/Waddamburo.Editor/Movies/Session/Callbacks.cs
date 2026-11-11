using System.Globalization;
using ImGuiNET;
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
            if (callbacks.IsEmpty)
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "This movie registers no callbacks.");
            ImGui.BeginChild("##callbacks", new(rect.Width * .62f, 0));
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
            ImGui.TextColored(new(.52f, .56f, .61f, 1), "numbers, true / false, null, anything else is text");
            foreach (var line in _log)
                ImGui.TextUnformatted(line);
            ImGui.EndChild();
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
