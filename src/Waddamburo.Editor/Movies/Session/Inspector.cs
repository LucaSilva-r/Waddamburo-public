using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _gesture; // A drag in a field is going on: its edits are one undo step.
    private bool _focusName; // Rename was asked for: the name field takes the keyboard.
    private const float LabelColumn = 104;

    // A property's name on the left, its field filling the rest; what it means on hover.
    private static void fieldLabel(string text, string? hint = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        if (hint is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
        ImGui.SameLine(LabelColumn);
    }

    private void layerFields(System.Text.Json.Nodes.JsonObject layer, int frame)
    {
        ImGui.BeginDisabled(_document!.ReadOnly);
        var keys = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().OrderBy(static key => (int)key["frame"]!).ToList();
        var onKey = keys.Any(key => (int)key["frame"]! == frame);
        // The values shown are the layer's on this frame, between its keys.
        var probe = (System.Text.Json.Nodes.JsonObject)layer.DeepClone();
        var current = MovieDocument.KeyAt(probe, frame);
        void drag(string property, float speed, string format, float width, float min = 0, float max = 0)
        {
            var value = (float)MovieDocument.Value(current, property)!;
            double? of(System.Text.Json.Nodes.JsonObject? key) => key is null ? null : MovieDocument.Value(key, property);
            // Among the keys that hold this property: the one on this frame, the one before, the one after.
            var held = keys.Where(key => MovieDocument.Has(key, property)).ToList();
            // Held by one key only, it does not animate: that is the clip's resting value, not a key to show.
            var at = held.Count < 2 ? null : held.FirstOrDefault(key => (int)key["frame"]! == frame);
            var previous = held.LastOrDefault(key => (int)key["frame"]! < frame);
            var next = held.FirstOrDefault(key => (int)key["frame"]! > frame);
            // Orange: keyed on this frame. Blue: on its way from one key to the next. Plain: not animating here.
            var from = at ?? previous;
            var moving = at is null && from is not null && next is not null && of(from) != of(next) && MovieDocument.Ease(from) != "hold";
            if (at is not null) ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.55f, .32f, .1f, 1));
            else if (moving) ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.16f, .3f, .46f, 1));
            ImGui.SetNextItemWidth(width);
            if (ImGui.DragFloat("##" + property, ref value, speed, min, max, format, max > min ? ImGuiSliderFlags.AlwaysClamp : ImGuiSliderFlags.None))
            {
                var continues = _gesture && !ImGui.IsItemActivated();
                _gesture = true;
                edited(() => _document.Edit(_ => setValues(layer, frame, (property, value)), continues));
            }
            if (at is not null || moving) ImGui.PopStyleColor();
            if (ImGui.IsItemDeactivated()) _gesture = false;
            if (ImGui.IsItemHovered() && !ImGui.IsItemActive() && (at is not null || moving))
                ImGui.SetTooltip(at is not null ? "Keyed on this frame. Right-click to remove it from the key."
                    : $"Animating: {number(of(from)!.Value)} on frame {(int)from!["frame"]!} to {number(of(next)!.Value)} on frame {(int)next!["frame"]!}.");
            if (ImGui.BeginPopupContextItem("##menu " + property))
            {
                void put(double wanted) => edited(() => _document.Edit(_ => keyValue(layer, frame, property, wanted)));
                if (ImGui.MenuItem("Key here", "", false, at is null))
                    put(value);
                if (ImGui.MenuItem("Remove from this key", "", false, at is not null))
                    edited(() => _document.Edit(_ => MovieDocument.RemoveChannel(layer, frame, property)));
                ImGui.Separator();
                if (ImGui.MenuItem(previous is null ? "Same as previous key" : $"Same as previous key  ({number(of(previous)!.Value)})", "", false,
                        previous is not null && value != (float)of(previous)!.Value))
                    put(of(previous)!.Value);
                if (ImGui.MenuItem(next is null ? "Same as next key" : $"Same as next key  ({number(of(next)!.Value)})", "", false,
                        next is not null && value != (float)of(next)!.Value))
                    put(of(next)!.Value);
                if (ImGui.MenuItem($"Default  ({number(MovieDocument.Default(property))})", "", false, value != (float)MovieDocument.Default(property)))
                    edited(() => _document.Edit(_ => setValues(layer, frame, (property, MovieDocument.Default(property)))));
                ImGui.Separator();
                if (ImGui.MenuItem("Stop animating (this value throughout)", "", false, held.Count > 1))
                    edited(() => _document.Edit(_ =>
                    {
                        // One value everywhere is one resting value: on the first key that held it.
                        MovieDocument.Set(held[0], property, value);
                        foreach (var key in held.Skip(1).ToArray()) MovieDocument.RemoveChannel(layer, (int)key["frame"]!, property);
                    }));
                ImGui.EndPopup();
            }
        }
        void pair(string label, string hint, string x, string y, float speed)
        {
            fieldLabel(label, hint);
            var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            drag(x, speed, "X  %.2f", half);
            ImGui.SameLine();
            drag(y, speed, "Y  %.2f", half);
        }

        if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            pair("Position", "Where it sits in its parent, in stage units. Drag a field, or double-click to type.", "x", "y", 1);
            pair("Scale", "1 is its own size.", "scaleX", "scaleY", .01f);
            fieldLabel("Rotation", "Degrees, clockwise.");
            drag("rotation", .5f, "%.1f deg", -1);
            fieldLabel("Pivot", "The point the stage handles rotate and scale about: 0 to 1 across its box (0.5 is the middle). An editor aid, not part of the movie.");
            var pivot = layer["editorPivot"] as System.Text.Json.Nodes.JsonArray;
            var pivotValue = pivot is { Count: 2 } ? new Vector2((float)MovieDocument.Number(pivot[0]), (float)MovieDocument.Number(pivot[1])) : new Vector2(.5f);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat2("##pivot", ref pivotValue, .01f, 0, 0, "%.2f"))
            {
                var continues = _gesture && !ImGui.IsItemActivated();
                _gesture = true;
                attempt(() => _document.Edit(_ => layer["editorPivot"] = new System.Text.Json.Nodes.JsonArray(pivotValue.X, pivotValue.Y), continues));
            }
            if (ImGui.IsItemDeactivated()) _gesture = false;
        }
        if (ImGui.CollapsingHeader("Appearance", ImGuiTreeNodeFlags.DefaultOpen))
        {
            fieldLabel("Opacity", "0 is invisible, 1 is solid.");
            drag("alpha", .01f, "%.2f", -1, 0, 1);
            appearanceFields(layer, current, frame);
        }
        if (ImGui.CollapsingHeader("Animation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextColored(onKey ? new Vector4(1, .56f, .18f, 1) : new(.52f, .56f, .61f, 1),
                onKey ? $"Key on frame {frame}" : $"Frame {frame}, between keys");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("A key holds only what was keyed on its frame. Orange fields: keyed here.\nBlue fields: on their way to their next key. Right-click a field to key it, take it out of the key, or copy a neighbour's value.");
            if (onKey && keys.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove key"))
                    edited(() => _document!.Edit(_ => layer["keys"]!.AsArray().Remove(keys.First(key => (int)key["frame"]! == frame))));
            }
            interpolationField(layer, frame);
            fieldLabel("Keys", "The frames this layer has keys on.");
            ImGui.TextWrapped(string.Join(", ", keys.Select(static key => (int)key["frame"]!)));
        }
        if (ImGui.CollapsingHeader("Layer"))
            layerLifetimeFields(layer, frame);
        ImGui.EndDisabled();
    }

    // The instance name on top, as in any inspector: it is what scripts and the game call the clip.
    private void nameField(System.Text.Json.Nodes.JsonObject layer)
    {
        var name = (string?)layer["name"] ?? "";
        ImGui.SetNextItemWidth(-1);
        if (_focusName)
        {
            ImGui.SetKeyboardFocusHere();
            _focusName = false;
        }
        ImGui.BeginDisabled(_document!.ReadOnly);
        if (ImGui.InputTextWithHint("##name", "instance name (scripts and the game use it)", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            edited(() => _document!.Edit(_ =>
            {
                if (name.Length == 0) layer.Remove("name"); else layer["name"] = name;
            }));
        ImGui.EndDisabled();
    }

    private void inspector(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo? clip)
    {
        if (panel("Inspector", rect))
        {
            if (_selectedAsset is not null)
                assetInspector();
            else if (clip is null)
                {
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "Nothing selected.");
                ImGui.Spacing();
                ImGui.TextWrapped("Click something on the stage or in the Clips list to see and change its position, look and animation here.");
                ImGui.TextWrapped("Click again on the same spot to reach what lies under it. Shift-drag on the stage selects several.");
            }
            else
            {
                if (_selection.Count > 1) ImGui.TextDisabled($"{_selection.Count} selected — inspecting the active object");
                void row(string label, string value)
                {
                    ImGui.TextColored(new(.52f, .56f, .61f, 1), label);
                    ImGui.SameLine(110);
                    ImGui.TextUnformatted(value);
                }
                if (ImGui.BeginTabBar("##inspector tabs"))
                {
                    if (ImGui.BeginTabItem("Properties"))
                    {
                        var source = layerOf(clips, clip);
                        var assetName = _document?.CharacterName(clip.CharacterId);
                        var kind = clip.Kind switch { LumenInstanceKind.Shape => "Shape", LumenInstanceKind.Text => "Text field", _ => "Sprite" };
                        if (source is { } named)
                            nameField(named.Layer);
                        ImGui.TextDisabled(clip.Level == 0 ? "The movie's root timeline" : assetName is null ? kind : $"{kind}: {assetName}");
                        if (clip.Kind == LumenInstanceKind.Sprite && clip.FrameCount > 1)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"  frame {clip.Frame} / {clip.FrameCount - 1}{(clip.Label is null ? "" : "  " + clip.Label)}");
                        }
                        if (clip.Kind == LumenInstanceKind.Sprite)
                        {
                            ImGui.BeginDisabled(clip.Path == _timelineContext.Path);
                            if (ImGui.Button("Open timeline")) openInstanceTimeline(clip.Path);
                            ImGui.EndDisabled();
                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                                ImGui.SetTooltip("Edit what is inside this sprite: its own layers and frames.\nHere you edit where the sprite sits in its parent.");
                            ImGui.SameLine();
                        }
                        if (assetName is not null)
                        {
                            var assetGroup = clip.Kind switch { LumenInstanceKind.Shape => "shapes", LumenInstanceKind.Text => "texts", _ => "sprites" };
                            if (ImGui.Button("Edit source")) selectAsset(assetGroup, assetName);
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The asset this is an instance of: its artwork, size or text style, shared by every instance.");
                            ImGui.SameLine();
                        }
                        var hidden = clip.HiddenByHost;
                        if (ImGui.Checkbox("Hide in editor", ref hidden))
                            player.InspectSetHidden(clip.Path, hidden);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Out of the way while you work. Not saved, not part of the movie.");
                        if (source is { } placed)
                            layerFields(placed.Layer, placed.Frame);
                        else if (_document is not null && clip.Level > 0)
                            ImGui.TextColored(new(.52f, .56f, .61f, 1), "Made by a script: nothing of it is in movie.json.");
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Info"))
                    {
                        var matrix = clip.Transform;
                        row("name", clip.Name.Length > 0 ? clip.Name : "(none)");
                        row("character", $"{clip.CharacterId} ({clip.Kind.ToString().ToLowerInvariant()})");
                        if (clip.ClassName is not null)
                            row("export", clip.ClassName);
                        row("depth", clip.Depth.ToString(CultureInfo.InvariantCulture));
                        row("position", $"{number(matrix.X)}, {number(matrix.Y)}");
                        row("scale", $"{number(MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12))}, {number(MathF.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22))}");
                        row("rotation", $"{number(MathF.Atan2(matrix.M12, matrix.M11) * 180 / MathF.PI)}°");
                        row("colour x", $"{number(clip.Multiply.Red)}, {number(clip.Multiply.Green)}, {number(clip.Multiply.Blue)}, alpha {number(clip.Multiply.Alpha)}");
                        row("colour +", $"{number(clip.Add.Red)}, {number(clip.Add.Green)}, {number(clip.Add.Blue)}, {number(clip.Add.Alpha)}");
                        row("blend", clip.BlendMode switch { 0 or 1 => "normal", 3 => "multiply", 4 => "screen", 8 => "add", var other => other.ToString(CultureInfo.InvariantCulture) });
                        row("visible", clip.Visible ? "yes" : "no (its script hid it)");
                        if (player.TryInspectBounds(clip.Path, out var box))
                            row("on stage", $"{number(box.X)}, {number(box.Y)}  {number(box.Width)} x {number(box.Height)}");
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
                var variables = player.InspectVariables(clip.Path);
                if (variables.Count > 0)
                {
                    ImGui.SeparatorText("script variables");
                    foreach (var (name, value) in variables)
                        row(name, value);
                }
            }
        }
        ImGui.End();
    }
}
