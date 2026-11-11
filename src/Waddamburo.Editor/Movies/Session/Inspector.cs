using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _gesture; // A drag in a field is going on: its edits are one undo step.
    private void layerFields(System.Text.Json.Nodes.JsonObject layer, int frame)
    {
        ImGui.BeginDisabled(_document!.ReadOnly);
        var keys = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().OrderBy(static key => (int)key["frame"]!).ToList();
        var onKey = keys.Any(key => (int)key["frame"]! == frame);
        ImGui.SeparatorText(onKey ? $"key on frame {frame}" : $"frame {frame}: a change makes a key here");
        // The values shown are the layer's on this frame, between its keys.
        var probe = (System.Text.Json.Nodes.JsonObject)layer.DeepClone();
        var current = MovieDocument.KeyAt(probe, frame);
        void vector(string label, string x, string y, float speed)
        {
            var value = new Vector2((float)MovieDocument.Value(current, x)!, (float)MovieDocument.Value(current, y)!);
            ImGui.SetNextItemWidth(-90);
            if (ImGui.DragFloat2(label, ref value, speed, 0, 0, "%.3g"))
            {
                var continues = _gesture && !ImGui.IsItemActivated();
                _gesture = true;
                edited(() => _document.Edit(_ =>
                {
                    var key = MovieDocument.KeyAt(layer, frame);
                    MovieDocument.Set(key, x, value.X);
                    MovieDocument.Set(key, y, value.Y);
                }, continues));
            }
            if (ImGui.IsItemDeactivated()) _gesture = false;
        }
        vector("Position", "x", "y", 1);
        vector("Scale", "scaleX", "scaleY", .01f);
        vector("Angle / alpha", "rotation", "alpha", .01f);
        var pivot = layer["editorPivot"] as System.Text.Json.Nodes.JsonArray;
        var pivotValue = pivot is { Count: 2 } ? new Vector2((float)pivot[0]!, (float)pivot[1]!) : new Vector2(.5f);
        ImGui.SetNextItemWidth(-90);
        if (ImGui.DragFloat2("Transform centre (0–1)", ref pivotValue, .01f, 0, 0, "%.2f"))
        {
            var continues = _gesture && !ImGui.IsItemActivated();
            _gesture = true;
            attempt(() => _document.Edit(_ => layer["editorPivot"] = new System.Text.Json.Nodes.JsonArray(pivotValue.X, pivotValue.Y), continues));
        }
        if (ImGui.IsItemDeactivated()) _gesture = false;
        appearanceFields(layer, current, frame);
        var name = (string?)layer["name"] ?? "";
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputText("name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            edited(() => _document!.Edit(_ =>
            {
                if (name.Length == 0) layer.Remove("name"); else layer["name"] = name;
            }));
        if (onKey && keys.Count > 1 && ImGui.Button("Remove this key"))
            edited(() => _document!.Edit(_ => layer["keys"]!.AsArray().Remove(keys.First(key => (int)key["frame"]! == frame))));
        ImGui.TextColored(new(.52f, .56f, .61f, 1), $"keys on frames {string.Join(", ", keys.Select(static key => (int)key["frame"]!))}");
        ImGui.EndDisabled();
    }

    private void inspector(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo? clip)
    {
        if (panel("Inspector", rect))
        {
            if (_selectedAsset is not null)
                assetInspector();
            else if (clip is null)
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "Select a clip or asset.");
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
                        var hidden = clip.HiddenByHost;
                        if (ImGui.Checkbox("hide", ref hidden))
                            player.InspectSetHidden(clip.Path, hidden);
                        if (layerOf(clips, clip) is { } source)
                            layerFields(source.Layer, source.Frame);
                        else if (_document is not null && clip.Level > 0)
                            ImGui.TextColored(new(.52f, .56f, .61f, 1), "Made by a script: nothing of it is in movie.json.");

                        if (clip is { Kind: LumenInstanceKind.Sprite, FrameCount: > 1 })
                        {
                            ImGui.SeparatorText("timeline");
                            var frame = clip.Frame;
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.SliderInt("##frame", ref frame, 0, clip.FrameCount - 1))
                                player.InspectGoto(clip.Path, frame, clip.Playing);
                            if (ImGui.Button(clip.Playing ? "Stop" : "Play"))
                                player.InspectSetPlaying(clip.Path, !clip.Playing);
                            foreach (var (label, at) in player.InspectLabels(clip.Path))
                            {
                                ImGui.SameLine();
                                if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(label).X + 20)
                                    ImGui.NewLine();
                                if (ImGui.SmallButton($"{label}##{at}"))
                                    player.InspectGoto(clip.Path, at, play: true);
                            }
                        }
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
