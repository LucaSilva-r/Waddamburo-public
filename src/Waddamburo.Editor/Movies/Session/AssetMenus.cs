using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private enum AssetDialog { Shape, Sprite, Text, Folder, RenameAsset, RenameFolder }
    private AssetDialog _assetDialog;
    private bool _openAssetDialog;
    private string _dialogTarget = "", _dialogFolder = "";

    private void requestAssetDialog(AssetDialog kind, string name, string target = "")
    {
        _assetDialog = kind;
        _newAssetName = name;
        _dialogTarget = target;
        _dialogFolder = _assetFolder;
        _openAssetDialog = true;
        if (kind == AssetDialog.Shape)
        {
            _newShapeTexture = _selectedAssetGroup == "textures" ? _selectedAsset : null;
            if (_newShapeTexture is not null && TextureOf?.Invoke(_newShapeTexture) is { } image)
                _newShapeSize = new(image.Width, image.Height);
        }
    }

    private void assetCreateMenu()
    {
        if (ImGui.MenuItem("Import PNG / SVG...", "", false, _assetFileDialog is null)) pickTexture();
        if (ImGui.MenuItem("Import folder...", "", false, _assetFileDialog is null))
        {
            _textureToReplace = null;
            _pickAssetFolder = true;
            _importFolder = _assetFolder;
            attempt(() => _assetFileDialog = _application.PickFolderAsync(_document!.Directory));
        }
        ImGui.Separator();
        if (ImGui.MenuItem("New folder...")) requestAssetDialog(AssetDialog.Folder, "New folder");
        if (ImGui.MenuItem("New shape...")) requestAssetDialog(AssetDialog.Shape, "artwork");
        if (ImGui.MenuItem("New text field...")) requestAssetDialog(AssetDialog.Text, "text");
        if (ImGui.MenuItem("New sprite...")) requestAssetDialog(AssetDialog.Sprite, "sprite");
    }

    private void assetContext(MovieAsset asset)
    {
        if (!ImGui.BeginPopupContextItem("##asset context")) return;
        selectAsset(asset.Group, asset.Name);
        ImGui.TextDisabled(asset.Name);
        ImGui.BeginDisabled(_document!.ReadOnly);
        if (ImGui.MenuItem("Rename...", "F2")) requestAssetDialog(AssetDialog.RenameAsset, asset.Name, asset.Id);
        if (asset.Group == "textures" && ImGui.MenuItem("Replace artwork...", "", false, _assetFileDialog is null)) pickTexture(asset.Name);
        if (ImGui.MenuItem("Open containing folder")) attempt(() => _document.OpenContainingFolder(asset.Group, asset.Name));
        moveMenu(asset.Folder, folder => _document.MoveAsset(asset.Group, asset.Name, folder));
        if (ImGui.MenuItem("Delete", "", false, _document.Uses(asset.Group, asset.Name).Count == 0))
            attempt(() => _document.Delete(asset.Group, asset.Name));
        ImGui.Separator();
        assetCreateMenu();
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void folderContext(MovieAssetFolder folder)
    {
        if (!ImGui.BeginPopupContextItem("##folder context")) return;
        ImGui.TextDisabled(folder.Name);
        if (ImGui.MenuItem("Open")) _navigateAssetFolder = folder.Id;
        ImGui.BeginDisabled(_document!.ReadOnly);
        if (ImGui.MenuItem("Rename...")) requestAssetDialog(AssetDialog.RenameFolder, folder.Name, folder.Id);
        moveMenu(folder.Parent, parent => _document.MoveFolder(folder.Id, parent), folder.Id);
        if (ImGui.MenuItem("Delete empty folder", "", false,
            !_document.Assets().Any(asset => asset.Folder == folder.Id) && !_document.Folders().Any(each => each.Parent == folder.Id)))
            attempt(() => _document.DeleteFolder(folder.Id));
        if (ImGui.MenuItem("Open package folder")) attempt(() => _document.OpenContainingFolder());
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void moveMenu(string current, Action<string> move, string? movingFolder = null)
    {
        if (!ImGui.BeginMenu("Move to")) return;
        if (ImGui.MenuItem("Assets (root)", "", current == "", current != "")) attempt(() => move(""));
        foreach (var folder in _document!.Folders().OrderBy(folder => _document.FolderPath(folder.Id), StringComparer.OrdinalIgnoreCase))
        {
            // The document also validates cycles if a folder was changed while this menu was open.
            if (folder.Id == movingFolder) continue;
            if (ImGui.MenuItem(_document.FolderPath(folder.Id) + "##" + folder.Id, "", folder.Id == current, folder.Id != current))
                attempt(() => move(folder.Id));
        }
        ImGui.EndMenu();
    }

    private void assetDialogs()
    {
        if (_openAssetDialog)
        {
            ImGui.OpenPopup("##asset dialog");
            _openAssetDialog = false;
        }
        if (!ImGui.BeginPopup("##asset dialog")) return;
        var rename = _assetDialog is AssetDialog.RenameAsset or AssetDialog.RenameFolder;
        ImGui.TextUnformatted(rename ? "Rename" : "Create " + _assetDialog.ToString().ToLowerInvariant());
        var enter = ImGui.InputText("Name", ref _newAssetName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        if (_assetDialog == AssetDialog.Shape)
        {
            textureChoice("Texture", _newShapeTexture, texture => _newShapeTexture = texture);
            ImGui.InputFloat2("Width / height", ref _newShapeSize);
        }
        if (_assetDialog == AssetDialog.RenameAsset)
            ImGui.TextUnformatted("Child instance names and script linkage names stay unchanged.");
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_newAssetName));
        if ((ImGui.Button(rename ? "Rename" : "Create") || enter) && attempt(applyAssetDialog))
            ImGui.CloseCurrentPopup();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void applyAssetDialog()
    {
        var document = _document!;
        var name = _newAssetName.Trim();
        switch (_assetDialog)
        {
            case AssetDialog.Folder:
                document.CreateFolder(name, _dialogFolder);
                break;
            case AssetDialog.RenameFolder:
                document.RenameFolder(_dialogTarget, name);
                break;
            case AssetDialog.RenameAsset:
                var asset = document.Assets().FirstOrDefault(asset => asset.Id == _dialogTarget)
                    ?? throw new InvalidOperationException("That asset no longer exists.");
                document.Rename(asset.Group, asset.Name, name);
                selectAsset(asset.Group, name);
                break;
            case AssetDialog.Shape:
                selectAsset("shapes", document.AddShape(name, _newShapeTexture, _newShapeSize.X, _newShapeSize.Y, folder: _dialogFolder));
                break;
            case AssetDialog.Text:
                selectAsset("texts", document.AddText(name, _dialogFolder));
                break;
            case AssetDialog.Sprite:
                selectAsset("sprites", document.AddSprite(name, folder: _dialogFolder));
                break;
        }
    }
}
