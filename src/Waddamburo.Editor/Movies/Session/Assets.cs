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

}
