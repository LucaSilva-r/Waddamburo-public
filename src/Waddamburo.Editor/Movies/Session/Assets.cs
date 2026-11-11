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
        // One row: make things, which kind is listed, find, how they are listed.
        assetActions();
        ImGui.SameLine();
        void view(string label, string hint, bool textures)
        {
            var chosen = _texturesView == textures;
            if (chosen) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.28f, .40f, .54f, 1));
            if (ImGui.Button(label)) _texturesView = textures;
            if (chosen) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
            ImGui.SameLine();
        }
        view("Assets", "What can be placed on a timeline: shapes (artwork), text fields and sprites (things with a timeline of their own).", false);
        view("Textures", "The image files of the package. A shape shows one; drag a texture onto the stage and its shape is made for you.", true);
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##asset filter", "Search", ref _assetFilter, 128);
        ImGui.SameLine();
        if (ImGui.Button(_assetGrid ? "List" : "Grid")) _assetGrid = !_assetGrid;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Switch between thumbnails and a plain list.");
        ImGui.SameLine();
        assetBreadcrumb();
        ImGui.BeginChild("##asset browser", Vector2.Zero);
        if (_dragAsset is not null)
            ImGui.SetTooltip("Drop on the stage to place it in the open timeline, or on a folder to move it.");
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
            if (entries.Length == 0 && !document.ReadOnly)
            {
                ImGui.TableNextColumn();
                ImGui.TextWrapped("Nothing here yet. Drop PNG or SVG files on the window, or right-click for New.");
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
        // Kept through the frame the button is let go on: the stage, drawn after this panel, takes the drop.
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) _dragAsset = null;
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

    private void drawAssetIcon(string name, Vector2 position, float size, uint? tint = null)
    {
        var draw = ImGui.GetWindowDrawList();
        if (IconOf?.Invoke(name) is { } texture)
            draw.AddImage(texture, position, position + new Vector2(size), Vector2.Zero, Vector2.One, tint ?? Text);
        else draw.AddText(position, tint ?? Text, name);
    }

    /// <summary>A button showing one of the editor's icons; what it does is said on hover.</summary>
    private bool iconButton(string icon, string hint, bool active = false, string? id = null, float size = 26, bool plain = false)
    {
        var start = ImGui.GetCursorScreenPos();
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.28f, .40f, .54f, 1));
        else if (plain) ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var pressed = ImGui.Button("##icon " + (id ?? icon), new(size + 4, size));
        if (active || plain) ImGui.PopStyleColor();
        var glyph = Math.Min(16, size - 4);
        drawAssetIcon(icon, start + new Vector2((size + 4 - glyph) / 2, (size - glyph) / 2), glyph);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(hint);
        return pressed;
    }
}
