using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private Task<string?>? _assetFileDialog;
    private string? _textureToReplace;
    private string _importFolder = "";
    private bool _pickAssetFolder;
    private string _newAssetName = "artwork";
    private string? _newShapeTexture;
    private Vector2 _newShapeSize = new(128, 128);

    /// <summary>Selects a package asset and opens its properties.</summary>
    public bool SelectAsset(string group, string name)
    {
        if (!AssetGroups.Contains(group) || _document?.Root[group]?[name] is null) return false;
        selectAsset(group, name);
        return true;
    }

    private void selectAsset(string group, string name)
    {
        _selectedAssetGroup = group;
        _texturesView = group == "textures";
        _selectedAsset = name;
        _selectedAssetId = _document?.AssetId(group, name);
        // One thing is selected at a time: an asset lets go of the clips and the keys.
        _selection.Clear();
        _script = null;
        _selected = _selectionAnchor = null;
        _keys.Clear();
        _key = null;
        _assetGesture = false;
    }

    private void importTexture(string path) => attempt(() =>
    {
        if (Directory.Exists(path))
        {
            var imported = _document!.ImportDirectory(path, _assetFolder);
            selectAsset("textures", imported[^1]);
        }
        else selectAsset("textures", _document!.ImportTexture(path, _assetFolder));
    });

    private void pickTexture(string? replace = null)
    {
        if (_assetFileDialog is not null || _document is not { ReadOnly: false }) return;
        _textureToReplace = replace is null ? null : _document.AssetId("textures", replace);
        _importFolder = _assetFolder;
        _pickAssetFolder = false;
        attempt(() => _assetFileDialog = _application.PickFileAsync(false, "PNG or SVG artwork", "png;svg", _document.Directory));
    }

    private void pollAssetFile()
    {
        if (_assetFileDialog is not { IsCompleted: true } dialog) return;
        _assetFileDialog = null;
        var replace = _textureToReplace;
        _textureToReplace = null;
        attempt(() =>
        {
            if (dialog.GetAwaiter().GetResult() is not { } path) return;
            if (_pickAssetFolder)
            {
                var imported = _document!.ImportDirectory(path, _importFolder);
                selectAsset("textures", imported[^1]);
            }
            else if (replace is null)
                selectAsset("textures", _document!.ImportTexture(path, _importFolder));
            else
            {
                var current = _document!.Assets().FirstOrDefault(asset => asset.Id == replace)
                    ?? throw new InvalidOperationException("The texture selected for replacement no longer exists.");
                _document.ReplaceTexture(current.Name, path);
                selectAsset("textures", current.Name);
            }
        });
    }

    private void assetActions()
    {
        ImGui.BeginDisabled(_document!.ReadOnly);
        if (iconButton("plus", "New shape, text field, sprite or folder; import PNG / SVG artwork.\nFiles dropped on the window are imported too.", id: "new asset"))
            ImGui.OpenPopup("##asset create menu");
        if (ImGui.BeginPopup("##asset create menu"))
        {
            assetCreateMenu();
            ImGui.EndPopup();
        }
        ImGui.EndDisabled();
    }

    private void textureChoice(string label, string? selected, Action<string?> choose)
    {
        if (!ImGui.BeginCombo(label, selected ?? "Native fill (host drawn)")) return;
        if (ImGui.Selectable("Native fill (host drawn)", selected is null)) choose(null);
        foreach (var entry in (_document!.Root["textures"] as JsonObject)?.ToArray() ?? [])
            if (ImGui.Selectable(entry.Key, entry.Key == selected)) choose(entry.Key);
        ImGui.EndCombo();
    }
}
