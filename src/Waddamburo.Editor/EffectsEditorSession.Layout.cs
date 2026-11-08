using ImGuiNET;
using Waddamburo.Catalog;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>Layout mode: the player's gameplay layout, every value constant (a single key at beat 0).</summary>
public sealed partial class EffectsEditorSession
{
    // The elements a layout can change: a two-player layout's second lane as *_p2 too.
    private readonly string[] _layoutTargets;

    // The layout's name (its file's), renamed by the app: null back when the name can't be used.
    private string _layoutName = "";
    private readonly Func<string, EffectEditorFiles?>? _renameLayout;

    private void LayoutToolbar()
    {
        if (_renameLayout is not null)
        {
            ImGui.SetNextItemWidth(180);
            var name = _layoutName;
            if (ImGui.InputText("##layout name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue) && name != _layoutName)
            {
                if (_renameLayout(name) is { } files) { _files = files; _layoutName = name.Trim(); _message = "Renamed"; _messageError = false; }
                else { _message = "That name can't be used (taken, or not a file name)"; _messageError = true; }
            }
            ImGui.SameLine();
        }
        ImGui.BeginDisabled(_files is null);
        if (ImGui.Button("Save (Ctrl+S)")) _saveRequested = true;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_history.CanUndo);
        if (ImGui.Button("Undo")) _undoRequested = true;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_history.CanRedo);
        if (ImGui.Button("Redo")) _redoRequested = true;
        ImGui.EndDisabled();
        ImGui.SameLine();
        // Back to the stock screen; undoable, and nothing changes on disk until Save.
        if (ImGui.Button("Reset layout")) Edit(EffectDocument.Empty, "Reset layout");
        ImGui.SameLine();
        // A 4:3 screen: songs draw the stage squeezed into 4:3 (camera.aspect 0.75); lay the elements out to fit.
        var fourThree = FourThree;
        if (ImGui.Checkbox("4:3 screen", ref fourThree))
        {
            // Squeezed to 3/4 of its width, each element keeps its shape at 3/4 of its height; the backgrounds stretch
            // (make them taller to fill). Turning it off gives the heights back.
            var values = new Dictionary<string, double> { ["camera.aspect"] = fourThree ? .75 : 1 };
            foreach (var target in _layoutTargets.Where(static t => ChartMods.BaseTarget(t) is not ("camera" or "background" or "fever" or "stage" or "notes")))
                values[target + ".scaley"] = _preview.ValueAt(target + ".scaley", 0d) * (fourThree ? .75 : 1 / .75);
            Edit(_history.Current.SetValuesAt(0, values, true), fourThree ? "4:3 screen" : "16:9 screen");
        }
    }

    // The scrubber: jump anywhere in the chart to see its notes under the layout.
    private void LayoutBar(EditorPanel rect)
    {
        if (Panel("Layout", rect, false))
        {
            var seconds = (float)_transport.Clock.Position.TotalSeconds;
            var end = (float)Math.Max(1, _sourceChart.Duration.TotalSeconds);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##scrub", ref seconds, 0, end, "%.1f s")) _transport.Seek(TimeSpan.FromSeconds(seconds));
            ImGui.TextColored(_messageError ? new(1, .4f, .35f, 1) : new(.6f, .68f, .75f, 1),
                _message ?? "Drag elements in the preview, or set values in the inspector. Save makes it your layout for songs without a modchart.");
        }
        ImGui.End();
    }

    private static string[] PropertiesOf(string target) => ChartMods.BaseTarget(target) switch
    {
        "notes" => [.. ChartMods.NoteProperties],
        "don" => [.. ChartMods.ElementProperties, .. ChartMods.DonModelProperties],
        _ => [.. ChartMods.ElementProperties],
    };

    // The selection's values: only the properties every selected element has (as Unity's inspector does); one that
    // differs between them shows "-", and setting it sets them all.
    private void LayoutValues(string target)
    {
        var targets = SelectedTargets.ToArray();
        if (targets.Length > 1) ImGui.TextWrapped($"{targets.Length} elements: {string.Join(", ", targets)}");
        var properties = targets.Skip(1).Aggregate((IEnumerable<string>)PropertiesOf(target), (shared, t) => shared.Intersect(PropertiesOf(t))).ToArray();
        foreach (var property in properties)
        {
            if (property == "depth")
            {
                LayoutDepth(targets, (float)_preview.ValueAt(target + ".depth", 0d));
                continue;
            }
            var values = targets.Select(t => _preview.ValueAt(t + "." + property, 0d)).ToArray();
            var mixed = values.Any(v => Math.Abs(v - values[0]) > 1e-6);
            var value = (float)values[0];
            ImGui.SetNextItemWidth(-64);
            var changed = ImGui.DragFloat(property, ref value, property is "x" or "y" or "rotation" or "yaw" or "pitch" or "roll" ? 1f : .01f,
                0, 0, mixed ? "-" : "%.3f");
            if (ImGui.IsItemActivated())
                BeginViewportDrag(GizmoMode.Slider, SelectedElement() ?? new(target, default, default, default, LumenMatrix.Identity),
                    EffectViewport.Fit(new(0, 0, 1280, 720)), default, property);
            if (changed && _viewportDrag is { Mode: GizmoMode.Slider })
                PreviewElementValues(targets.ToDictionary(t => t + "." + property, _ => (double)value));
            if (ImGui.IsItemDeactivated()) EndDrag();
        }
        ImGui.Separator();
    }

    // Draw order in whole steps: a higher depth is drawn in front, equals keep the game's order.
    // Several selected: each moves one step from its own depth.
    private void LayoutDepth(string[] targets, float value)
    {
        var depth = (int)Math.Round(value);
        void step(int by) => Edit(_history.Current.SetValuesAt(0, targets.ToDictionary(t => t + ".depth",
            t => Math.Round(_preview.ValueAt(t + ".depth", 0d)) + by), true), by < 0 ? "Send back" : "Bring forward");
        if (ImGui.Button("Send back")) step(-1);
        ImGui.SameLine();
        if (ImGui.Button("Bring forward")) step(1);
        ImGui.SameLine();
        ImGui.TextUnformatted(targets.Length > 1 ? "depth" : $"depth {depth}");
    }
}
