using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>Layout mode: the player's gameplay layout, every value constant (a single key at beat 0).</summary>
public sealed partial class EffectsEditorSession
{
    private void LayoutToolbar()
    {
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
            foreach (var target in ChartMods.Elements.Where(static t => t is not ("camera" or "background" or "fever" or "stage")))
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

    private void LayoutValues(string target)
    {
        string[] properties = target == "notes" ? [.. ChartMods.NoteProperties]
            : target == "don" ? [.. ChartMods.ElementProperties, .. ChartMods.DonModelProperties] : [.. ChartMods.ElementProperties];
        foreach (var property in properties)
        {
            var value = (float)_preview.ValueAt(target + "." + property, 0d);
            ImGui.SetNextItemWidth(-64);
            if (property == "depth")
            {
                LayoutDepth(target, value);
                continue;
            }
            var changed = ImGui.DragFloat(property, ref value, property is "x" or "y" or "rotation" or "yaw" or "pitch" or "roll" ? 1f : .01f);
            if (ImGui.IsItemActivated())
                BeginViewportDrag(GizmoMode.Slider, SelectedElement() ?? new(target, default, default, default, LumenMatrix.Identity),
                    EffectViewport.Fit(new(0, 0, 1280, 720)), default, property);
            if (changed && _viewportDrag is { Mode: GizmoMode.Slider }) PreviewElementValues(new() { [target + "." + property] = value });
            if (ImGui.IsItemDeactivated()) EndDrag();
        }
        ImGui.Separator();
    }

    // Draw order in whole steps: a higher depth is drawn in front, equals keep the game's order.
    private void LayoutDepth(string target, float value)
    {
        var depth = (int)Math.Round(value);
        void set(int to) => Edit(_history.Current.SetValuesAt(0, new Dictionary<string, double> { [target + ".depth"] = to }, true),
            $"{target} depth {to}");
        if (ImGui.Button("Send back")) set(depth - 1);
        ImGui.SameLine();
        if (ImGui.Button("Bring forward")) set(depth + 1);
        ImGui.SameLine();
        ImGui.TextUnformatted($"depth {depth}");
    }
}
