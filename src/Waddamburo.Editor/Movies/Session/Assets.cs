using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private string? _selectedAsset, _selectedAssetId;
    private string _selectedAssetGroup = "texts";
    private bool _assetGrid = true, _texturesView;
    private string _assetFilter = "", _assetFolder = "";
    private string? _navigateAssetFolder, _dragAsset;
    private static readonly string[] AssetCharacterGroups = ["texts", "shapes", "sprites"];
    private static readonly string[] AssetGroups = ["textures", .. AssetCharacterGroups];
    private string _newTextName = "";
    private readonly Dictionary<string, string> _assetNames = new(StringComparer.Ordinal);

    private void refreshAssetSelection()
    {
        if (_document is null || _document.ReadOnly) return;
        var assets = _document.Assets();
        foreach (var asset in assets)
        {
            if (asset.Group == "sprites" && _assetNames.TryGetValue(asset.Id, out var previous) && previous != asset.Name)
            {
                if (_shown == previous) _shown = asset.Name;
                for (var i = 0; i < _trail.Count; i++)
                    if (_trail[i] == previous) _trail[i] = asset.Name;
            }
            _assetNames[asset.Id] = asset.Name;
        }
        if (_selectedAssetId is not null)
        {
            var asset = assets.FirstOrDefault(asset => asset.Id == _selectedAssetId);
            _selectedAsset = asset?.Name;
            if (asset is not null) _selectedAssetGroup = asset.Group;
        }
        if (_assetFolder.Length > 0 && !_document.Folders().Any(folder => folder.Id == _assetFolder)) _assetFolder = "";
    }

    private void assets()
    {
        var document = _document!;
        assetActions();
        ImGui.SameLine();
        if (ImGui.RadioButton("Assets", !_texturesView)) _texturesView = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Textures", _texturesView)) _texturesView = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Grid", _assetGrid)) _assetGrid = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("List", !_assetGrid)) _assetGrid = false;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##asset filter", "Search assets", ref _assetFilter, 128);
        assetBreadcrumb();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##new text", "New field name", ref _newTextName, 128);
        ImGui.SameLine();
        var fieldName = _newTextName.Trim();
        ImGui.BeginDisabled(document.ReadOnly || fieldName.Length == 0 || document.Exists("texts", fieldName));
        if (ImGui.Button("Create text field"))
        {
            attempt(() => selectAsset("texts", document.AddText(fieldName, _assetFolder)));
            _newTextName = "";
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Drop PNG / SVG files or folders here. Right-click for actions.");
        ImGui.BeginChild("##asset browser", Vector2.Zero);
        var tileHovered = false;
        var columns = _assetGrid ? Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 140)) : 1;
        if (ImGui.BeginTable("##asset table", columns))
        {
            foreach (var folder in document.Folders().Where(folder => _assetFilter.Length > 0
                ? folder.Name.Contains(_assetFilter, StringComparison.OrdinalIgnoreCase) : folder.Parent == _assetFolder)
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TableNextColumn();
                ImGui.PushID(folder.Id);
                var origin = ImGui.GetCursorScreenPos();
                var size = new Vector2(ImGui.GetContentRegionAvail().X, _assetGrid ? 110 : 26);
                ImGui.Selectable("##folder", false, ImGuiSelectableFlags.AllowDoubleClick, size);
                tileHovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) _navigateAssetFolder = folder.Id;
                assetDropTarget(folder.Id);
                folderContext(folder);
                drawAssetTile(origin, size, folder.Name, "folder", "folder", null, null);
                ImGui.PopID();
            }
            var entries = document.ReadOnly
                ? AssetGroups.SelectMany(group => (document.Root[group] as JsonObject ?? []).Select(entry => new MovieAsset(group + "/" + entry.Key, group, entry.Key, ""))).ToArray()
                : document.Assets().ToArray();
            foreach (var asset in entries.Where(asset => (asset.Group == "textures") == _texturesView && (_assetFilter.Length > 0
                ? asset.Name.Contains(_assetFilter, StringComparison.OrdinalIgnoreCase) : asset.Folder == _assetFolder)))
            {
                ImGui.TableNextColumn();
                ImGui.PushID(asset.Id);
                var origin = ImGui.GetCursorScreenPos();
                var size = new Vector2(ImGui.GetContentRegionAvail().X, _assetGrid ? 110 : 26);
                if (ImGui.Selectable("##asset", _selectedAssetId == asset.Id || (_selectedAssetGroup == asset.Group && _selectedAsset == asset.Name), ImGuiSelectableFlags.None, size))
                    selectAsset(asset.Group, asset.Name);
                tileHovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !document.ReadOnly) _dragAsset = asset.Id;
                assetContext(asset);
                var icon = asset.Group switch { "textures" => "image", "shapes" => "shapes", "texts" => "type", _ => "clapperboard" };
                drawAssetTile(origin, size, asset.Name, asset.Group[..^1], icon, asset.Group, document.Root[asset.Group]?[asset.Name]);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (!document.ReadOnly && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput
            && ImGui.IsKeyPressed(ImGuiKey.F2) && document.Assets().FirstOrDefault(asset => asset.Id == _selectedAssetId) is { } renameAsset)
            requestAssetDialog(AssetDialog.RenameAsset, renameAsset.Name, renameAsset.Id);
        // EndTable registers its full rectangle as an item, including empty cells. Only tiles block this menu.
        if (!tileHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right)
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            ImGui.OpenPopup("##asset background");
        if (ImGui.BeginPopup("##asset background"))
        {
            ImGui.BeginDisabled(document.ReadOnly);
            assetCreateMenu();
            ImGui.Separator();
            if (ImGui.MenuItem("Open package folder")) attempt(() => document.OpenContainingFolder());
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        ImGui.EndChild();
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _dragAsset = null;
        if (_navigateAssetFolder is { } destination)
        {
            _assetFolder = destination;
            _navigateAssetFolder = null;
            _assetFilter = "";
        }
        assetDialogs();
    }

    private void assetBreadcrumb()
    {
        if (ImGui.SmallButton("Assets##root")) _navigateAssetFolder = "";
        assetDropTarget("");
        var trail = new List<MovieAssetFolder>();
        var folder = _document!.Folders().FirstOrDefault(folder => folder.Id == _assetFolder);
        var visited = new HashSet<string>();
        while (folder is not null && visited.Add(folder.Id))
        {
            trail.Insert(0, folder);
            folder = _document.Folders().FirstOrDefault(each => each.Id == folder.Parent);
        }
        foreach (var part in trail)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(">");
            ImGui.SameLine();
            if (ImGui.SmallButton(part.Name + "##" + part.Id)) _navigateAssetFolder = part.Id;
            assetDropTarget(part.Id);
        }
    }

    private void assetDropTarget(string folder)
    {
        if (_dragAsset is null || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) return;
        ImGui.SetTooltip("Move asset here");
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _document!.Assets().FirstOrDefault(asset => asset.Id == _dragAsset) is { } asset)
            attempt(() => _document.MoveAsset(asset.Group, asset.Name, folder));
    }

    private void drawAssetTile(Vector2 origin, Vector2 size, string name, string kind, string icon, string? group, JsonNode? entry)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        var top = origin + new Vector2(8, 5);
        var end = origin + new Vector2(size.X - 8, 78);
        if (_assetGrid)
        {
            draw.AddRectFilled(top, end, color(.10f, .11f, .13f), 4);
            if (group == "textures") texturePreview(name, top, end);
            else if (group == "shapes" && entry is JsonObject shape) shapePreview(shape, top, end);
            else drawAssetIcon(icon, (top + end) / 2 - new Vector2(16), 32);
            draw.AddText(origin + new Vector2(8, 82), Text, name);
            draw.AddText(origin + new Vector2(8, 96), Muted, kind);
        }
        else
        {
            drawAssetIcon(icon, origin + new Vector2(4), 18);
            draw.AddText(origin + new Vector2(32, 4), Text, name + "  (" + kind + ")");
        }
        draw.PopClipRect();
    }

    private void drawAssetIcon(string name, Vector2 position, float size)
    {
        var draw = ImGui.GetWindowDrawList();
        if (IconOf?.Invoke(name) is { } texture)
            draw.AddImage(texture, position, position + new Vector2(size), Vector2.Zero, Vector2.One, Text);
        else draw.AddText(position, Text, name);
    }
}
