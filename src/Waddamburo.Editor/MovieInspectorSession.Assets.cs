using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private string? _selectedAsset;
    private string _selectedAssetGroup = "texts";
    private bool _assetGrid = true;
    private string _assetFilter = "";
    private static readonly string[] AssetCharacterGroups = ["texts", "shapes", "sprites"];
    private string _newTextName = "";

    private void assets()
    {
        var root = _document!.Root;
        if (ImGui.RadioButton("Grid", _assetGrid)) _assetGrid = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("List", !_assetGrid)) _assetGrid = false;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##asset filter", "Search assets", ref _assetFilter, 128);
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##new text", "New field name", ref _newTextName, 128);
        ImGui.SameLine();
        var name = _newTextName.Trim();
        var available = name.Length > 0 && AssetCharacterGroups.All(group => root[group]?[name] is null);
        ImGui.BeginDisabled(!available);
        if (ImGui.Button("Create text field"))
        {
            edited(() => _document.Edit(document =>
            {
                if (document["texts"] is not JsonObject fields)
                    document["texts"] = fields = new JsonObject();
                fields[name] = new JsonObject { ["rect"] = new JsonArray(0, 0, 200, 48), ["size"] = 32,
                    ["align"] = "left", ["color"] = "#ffffff" };
            }));
            _newTextName = "";
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("Add a field to a sprite with Timeline > Add layer. Scripts set its text.");
        ImGui.BeginChild("##asset browser", Vector2.Zero);
        var columns = _assetGrid ? Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 140)) : 1;
        if (ImGui.BeginTable("##asset table", columns))
        {
            foreach (var group in AssetCharacterGroups)
                foreach (var field in (root[group] as JsonObject)?.ToArray() ?? [])
                {
                    if (!field.Key.Contains(_assetFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    ImGui.TableNextColumn();
                    ImGui.PushID(group + "/" + field.Key);
                    var origin = ImGui.GetCursorScreenPos();
                    var selected = _selectedAsset == field.Key && _selectedAssetGroup == group;
                    var size = new Vector2(ImGui.GetContentRegionAvail().X, _assetGrid ? 110 : 26);
                    if (ImGui.Selectable("##asset", selected, ImGuiSelectableFlags.None, size))
                    {
                        _selectedAsset = field.Key;
                        _selectedAssetGroup = group;
                        _assetGesture = false;
                    }
                    var draw = ImGui.GetWindowDrawList();
                    draw.PushClipRect(origin, origin + size, true);
                    var icon = group switch { "texts" => "T", "shapes" => "[]", _ => "[>]" };
                    if (_assetGrid)
                    {
                        var top = origin + new Vector2(8, 5);
                        var end = origin + new Vector2(size.X - 8, 78);
                        draw.AddRectFilled(top, end, color(.10f, .11f, .13f), 4);
                        if (group == "texts" && field.Value is JsonObject text)
                        {
                            var hex = (string?)text["color"] ?? "#000000";
                            var rgb = Convert.ToUInt32(hex[1..], 16);
                            var tint = color((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);
                            var sampleSize = Math.Clamp((float?)text["size"] ?? 32, 12, 44);
                            draw.AddText(ImGui.GetFont(), sampleSize, top + new Vector2(12, 12), tint, "Aa 12");
                        }
                        else if (group == "shapes" && field.Value is JsonObject shape)
                            shapePreview(shape, top, end);
                        else
                            draw.AddText(top + new Vector2(12, 20), Text, icon);
                        draw.AddText(origin + new Vector2(8, 82), Text, field.Key);
                        draw.AddText(origin + new Vector2(8, 96), Muted, group[..^1]);
                    }
                    else
                    {
                        draw.AddText(origin + new Vector2(4, 4), KeySelected, icon);
                        draw.AddText(origin + new Vector2(38, 4), Text, field.Key + "  (" + group[..^1] + ")");
                    }
                    draw.PopClipRect();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(field.Key + " (" + group[..^1] + ")");
                    ImGui.PopID();
                }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    // A package shape is one textured quad. Preserve its authored proportions and atlas region.
    private void shapePreview(JsonObject shape, Vector2 top, Vector2 end)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(top, end, true);
        for (var y = top.Y; y < end.Y; y += 10)
            for (var x = top.X; x < end.X; x += 10)
            {
                var light = ((int)((x - top.X) / 10) + (int)((y - top.Y) / 10)) % 2 == 0;
                draw.AddRectFilled(new(x, y), Vector2.Min(new(x + 10, y + 10), end),
                    light ? color(.24f, .25f, .27f) : color(.17f, .18f, .20f));
            }
        var textureName = (string?)shape["texture"];
        var texture = textureName is null ? null : TextureOf?.Invoke(textureName);
        if (texture is { } image && shape["rect"] is JsonArray bounds && bounds.Count == 4)
        {
            var width = Math.Abs((float)bounds[2]!);
            var height = Math.Abs((float)bounds[3]!);
            if (width > 0 && height > 0)
            {
                var scale = Math.Min(Math.Max(0, end.X - top.X - 8) / width, Math.Max(0, end.Y - top.Y - 8) / height);
                var size = new Vector2(width, height) * scale;
                var start = (top + end - size) / 2;
                var uv = shape["uv"] as JsonArray;
                var first = uv is { Count: 4 } ? new Vector2((float)uv[0]!, (float)uv[1]!) : Vector2.Zero;
                var last = uv is { Count: 4 } ? new Vector2((float)uv[2]!, (float)uv[3]!) : Vector2.One;
                draw.AddImage(image.Texture, start, start + size, first, last);
            }
        }
        else
            draw.AddText(top + new Vector2(6, 6), Text, textureName is null ? "Native fill" : "Texture unavailable");
        draw.PopClipRect();
    }

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
