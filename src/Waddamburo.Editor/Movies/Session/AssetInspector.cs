using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _assetGesture;
    private uint _assetWidget;

    private void assetGesture()
    {
        if (ImGui.IsItemActivated())
            _assetWidget = ImGui.GetItemID();
        if (ImGui.GetItemID() == _assetWidget)
            _assetGesture = ImGui.IsItemActive() && !ImGui.IsItemDeactivated();
    }

    private void assetInspector()
    {
        var root = _document!.Root;
        var name = _selectedAsset!;
        if (root[_selectedAssetGroup]?[name] is not JsonObject entry)
        {
            ImGui.TextDisabled("This asset no longer exists. Undo to restore it.");
            return;
        }
        if (_selectedAssetGroup != "texts")
        {
            ImGui.SeparatorText(name + " (" + _selectedAssetGroup[..^1] + ")");
            if (_selectedAssetGroup == "sprites")
            {
                ImGui.TextUnformatted($"Frames: {(int?)entry["frames"] ?? 1}");
                ImGui.TextUnformatted($"Layers: {(entry["layers"] as JsonArray)?.Count ?? 0}");
            }
            else
            {
                var previewOrigin = ImGui.GetCursorScreenPos();
                var previewSize = new Vector2(Math.Max(32, ImGui.GetContentRegionAvail().X), 160);
                shapePreview(entry, previewOrigin, previewOrigin + previewSize);
                ImGui.Dummy(previewSize);
                ImGui.TextUnformatted($"Texture: {(string?)entry["texture"] ?? "Native fill"}");
                if (entry["rect"] is JsonArray bounds && bounds.Count == 4)
                    ImGui.TextUnformatted($"Size: {bounds[2]} x {bounds[3]}");
            }
            return;
        }
        ImGui.PushID(name);
        ImGui.SeparatorText(name + " (Text)");
        var users = (root["sprites"] as JsonObject ?? []).SelectMany(sprite =>
            (sprite.Value?["layers"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(layer => (string?)layer["character"] == name)
                .Select(layer => $"{sprite.Key}: {(string?)layer["name"] ?? name}")).ToArray();
                foreach (var user in users)
                    ImGui.TextUnformatted(user);
                if (entry["rect"] is JsonArray rect && rect.Count == 4)
                {
                    var box = new Vector4((float)rect[0]!, (float)rect[1]!, (float)rect[2]!, (float)rect[3]!);
                    if (ImGui.InputFloat4("Box (x, y, width, height)", ref box) && box.Z > 0 && box.W > 0)
                        edited(() => _document.Edit(_ => entry["rect"] = new JsonArray(box.X, box.Y, box.Z, box.W), _assetGesture && !ImGui.IsItemActivated()));
                }
                assetGesture();
                var size = (float?)entry["size"] ?? 32;
                if (ImGui.InputFloat("Font size", ref size) && size > 0)
                    edited(() => _document.Edit(_ => entry["size"] = size, _assetGesture && !ImGui.IsItemActivated()));
                assetGesture();
                var alignment = (string?)entry["align"] ?? "left";
                if (ImGui.BeginCombo("Alignment", alignment))
                {
                    foreach (var option in new[] { "left", "center", "right" })
                        if (ImGui.Selectable(option, alignment == option))
                            edited(() => _document.Edit(_ => entry["align"] = option));
                    ImGui.EndCombo();
                }
                var hex = (string?)entry["color"] ?? "#000000";
                var rgb = Convert.ToUInt32(hex[1..], 16);
                var colour = new Vector3((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);
                if (ImGui.ColorEdit3("Colour", ref colour))
                    edited(() => _document.Edit(_ => entry["color"] = $"#{(int)MathF.Round(colour.X * 255):x2}{(int)MathF.Round(colour.Y * 255):x2}{(int)MathF.Round(colour.Z * 255):x2}", _assetGesture && !ImGui.IsItemActivated()));
                assetGesture();
                ImGui.BeginDisabled(users.Length > 0);
                if (ImGui.Button("Delete unused field"))
                    edited(() => _document.Edit(_ => root["texts"]!.AsObject().Remove(name)));
                ImGui.EndDisabled();
        ImGui.PopID();
    }
}
