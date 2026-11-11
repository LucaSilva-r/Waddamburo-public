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
        inspectAsset(root, name);
    }

    private void inspectAsset(JsonObject root, string name)
    {
        var group = _selectedAssetGroup;
        if (group == "textures" && root["textures"]?[name] is JsonValue file)
        {
            ImGui.SeparatorText(name + " (Texture)");
            var origin = ImGui.GetCursorScreenPos();
            var thumbnailSize = new Vector2(Math.Max(32, ImGui.GetContentRegionAvail().X), 160);
            texturePreview(name, origin, origin + thumbnailSize);
            ImGui.Dummy(thumbnailSize);
            ImGui.TextWrapped(file.GetValue<string>());
            if (TextureOf?.Invoke(name) is { } image)
                ImGui.TextUnformatted($"{image.Width} x {image.Height}");
            ImGui.BeginDisabled(_document!.ReadOnly || _assetFileDialog is not null);
            if (ImGui.Button("Replace artwork...")) pickTexture(name);
            ImGui.EndDisabled();
            ImGui.TextWrapped("Replacement keeps the texture name and every shape using it. Undo restores the previous artwork.");
            assetUsers(group, name);
            return;
        }
        if (root[group]?[name] is not JsonObject entry)
        {
            ImGui.TextDisabled("This asset no longer exists. Undo to restore it.");
            return;
        }
        if (group != "texts")
        {
            ImGui.SeparatorText(name + " (" + group[..^1] + ")");
            if (group == "sprites")
            {
                ImGui.TextUnformatted($"Frames: {(int?)entry["frames"] ?? 1}");
                ImGui.TextUnformatted($"Layers: {(entry["layers"] as JsonArray)?.Count ?? 0}");
                if (ShowSprite is not null && ImGui.Button("Open sprite in isolation")) show(name);
            }
            else
            {
                var previewOrigin = ImGui.GetCursorScreenPos();
                var previewSize = new Vector2(Math.Max(32, ImGui.GetContentRegionAvail().X), 160);
                shapePreview(entry, previewOrigin, previewOrigin + previewSize);
                ImGui.Dummy(previewSize);
                shapeFields(name, entry);
            }
            assetUsers(group, name);
            return;
        }
        ImGui.PushID(name);
        ImGui.BeginDisabled(_document!.ReadOnly);
        ImGui.SeparatorText(name + " (Text)");
        var users = (root["sprites"] as JsonObject ?? []).SelectMany(sprite =>
            (sprite.Value?["layers"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(layer => (string?)layer["character"] == name)
                .Select(layer => $"{sprite.Key}: {(string?)layer["name"] ?? name}")).ToArray();
                foreach (var user in users)
                    ImGui.TextUnformatted(user);
                if (entry["rect"] is JsonArray rect && rect.Count == 4)
                {
                    var box = new Vector4(assetNumber(rect[0]), assetNumber(rect[1]), assetNumber(rect[2]), assetNumber(rect[3]));
                    if (ImGui.InputFloat4("Box (x, y, width, height)", ref box) && box.Z > 0 && box.W > 0)
                        edited(() => _document!.Edit(_ => entry["rect"] = new JsonArray(box.X, box.Y, box.Z, box.W), _assetGesture && !ImGui.IsItemActivated()));
                }
                assetGesture();
                var size = entry["size"] is { } fontSize ? assetNumber(fontSize) : 32;
                if (ImGui.InputFloat("Font size", ref size) && size > 0)
                    edited(() => _document!.Edit(_ => entry["size"] = size, _assetGesture && !ImGui.IsItemActivated()));
                assetGesture();
                var alignment = (string?)entry["align"] ?? "left";
                if (ImGui.BeginCombo("Alignment", alignment))
                {
                    foreach (var option in new[] { "left", "center", "right" })
                        if (ImGui.Selectable(option, alignment == option))
                            edited(() => _document!.Edit(_ => entry["align"] = option));
                    ImGui.EndCombo();
                }
                var hex = (string?)entry["color"] ?? "#000000";
                var rgb = Convert.ToUInt32(hex[1..], 16);
                var colour = new Vector3((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);
                if (ImGui.ColorEdit3("Colour", ref colour))
                    edited(() => _document!.Edit(_ => entry["color"] = $"#{(int)MathF.Round(colour.X * 255):x2}{(int)MathF.Round(colour.Y * 255):x2}{(int)MathF.Round(colour.Z * 255):x2}", _assetGesture && !ImGui.IsItemActivated()));
                assetGesture();
                ImGui.BeginDisabled(users.Length > 0);
                if (ImGui.Button("Delete unused field"))
                    edited(() => _document!.Edit(_ => root["texts"]!.AsObject().Remove(name)));
                ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private void assetUsers(string group, string name)
    {
        ImGui.SeparatorText("Used by");
        var users = _document!.Uses(group, name);
        if (group == "textures")
        {
            foreach (var shape in (_document.Root["shapes"] as JsonObject ?? [])
                .Where(shape => (string?)shape.Value?["texture"] == name))
                if (ImGui.SmallButton(shape.Key + "##source shape")) selectAsset("shapes", shape.Key);
        }
        else foreach (var user in users) ImGui.TextWrapped(user);
        if (users.Count == 0) ImGui.TextDisabled("Unused. Add a layer or assign this texture to a shape.");
        ImGui.BeginDisabled(_document.ReadOnly || users.Count > 0);
        if (ImGui.Button("Delete unused asset")) attempt(() => _document.Delete(group, name));
        ImGui.EndDisabled();
    }

    private void shapeFields(string name, JsonObject shape)
    {
        ImGui.BeginDisabled(_document!.ReadOnly);
        textureChoice("Texture", (string?)shape["texture"], texture => attempt(() => _document!.SetShapeTexture(name, texture)));
        ImGui.EndDisabled();
        if ((string?)shape["texture"] is { } source && ImGui.Button("Source texture")) selectAsset("textures", source);
        ImGui.BeginDisabled(_document.ReadOnly);
        if (shape["rect"] is JsonArray rect && rect.Count == 4)
        {
            var box = new Vector4(assetNumber(rect[0]), assetNumber(rect[1]), assetNumber(rect[2]), assetNumber(rect[3]));
            ImGui.TextUnformatted("Bounds (x, y, width, height)");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat4("##shape bounds", ref box))
                attempt(() => _document!.SetShapeRect(name, box.X, box.Y, box.Z, box.W, _assetGesture && !ImGui.IsItemActivated()));
            assetGesture();
        }
        var uv = shape["uv"] as JsonArray;
        var region = uv is { Count: 4 }
            ? new Vector4(assetNumber(uv[0]), assetNumber(uv[1]), assetNumber(uv[2]), assetNumber(uv[3])) : new Vector4(0, 0, 1, 1);
        ImGui.TextUnformatted("Texture region (left, top, right, bottom)");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputFloat4("##shape region", ref region))
            attempt(() => _document!.SetShapeUv(name, [region.X, region.Y, region.Z, region.W], _assetGesture && !ImGui.IsItemActivated()));
        assetGesture();
        ImGui.BeginDisabled(uv is null);
        if (ImGui.Button("Use whole texture")) attempt(() => _document!.SetShapeUv(name, null));
        ImGui.EndDisabled();
        ImGui.TextWrapped("Bounds and artwork affect every instance of this shape. Instance names and animation stay unchanged.");
        ImGui.EndDisabled();
    }
}
