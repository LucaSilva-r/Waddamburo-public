using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Waddamburo.Editor;

public sealed record MovieAsset(string Id, string Group, string Name, string Folder);
public sealed record MovieAssetFolder(string Id, string Name, string Parent);

public sealed partial class MovieDocument
{
    public const string MetadataFileName = "movie.editor.json";
    public static readonly string[] AssetGroups = ["textures", .. CharacterGroups];

    private JsonObject assetMetadata => _metadata["assets"] as JsonObject ?? [];
    private JsonObject folderMetadata => _metadata["folders"] as JsonObject ?? [];

    /// <summary>Stable identities and browser folders. Legacy packages acquire IDs in memory; the first edit saves them.</summary>
    public IReadOnlyList<MovieAsset> Assets() => [.. assetMetadata.Select(entry => new MovieAsset(entry.Key,
        (string)entry.Value!["group"]!, (string)entry.Value!["name"]!, (string?)entry.Value!["folder"] ?? ""))];

    public IReadOnlyList<MovieAssetFolder> Folders() => [.. folderMetadata.Select(entry => new MovieAssetFolder(entry.Key,
        (string)entry.Value!["name"]!, (string?)entry.Value!["parent"] ?? ""))];

    public string? AssetId(string group, string name) => Assets().FirstOrDefault(asset => asset.Group == group && asset.Name == name)?.Id;

    private void syncAssetIds()
    {
        _metadata["version"] = 1;
        if (_metadata["assets"] is not JsonObject) _metadata["assets"] = new JsonObject();
        if (_metadata["folders"] is not JsonObject) _metadata["folders"] = new JsonObject();
        var registered = new HashSet<(string, string)>();
        foreach (var entry in Assets())
            if (!AssetGroups.Contains(entry.Group) || group(entry.Group)[entry.Name] is null)
                assetMetadata.Remove(entry.Id);
            else registered.Add((entry.Group, entry.Name));
        foreach (var assetGroup in AssetGroups)
            foreach (var entry in group(assetGroup))
                if (registered.Add((assetGroup, entry.Key)))
                    assetMetadata[Guid.NewGuid().ToString("N")] = new JsonObject
                        { ["group"] = assetGroup, ["name"] = entry.Key, ["folder"] = "" };
    }

    private void requireFolder(string id)
    {
        if (id.Length > 0 && folderMetadata[id] is null)
            throw new InvalidOperationException("That asset folder no longer exists.");
    }

    private void validateFolderName(string name, string parent, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new InvalidOperationException("A folder needs a name without slashes.");
        if (Folders().Any(folder => folder.Id != except && folder.Parent == parent && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A folder named '{name}' already exists here.");
    }

    public string CreateFolder(string name, string parent = "")
    {
        requireFolder(parent);
        validateFolderName(name, parent);
        var id = Guid.NewGuid().ToString("N");
        Edit(_ => folderMetadata[id] = new JsonObject { ["name"] = name, ["parent"] = parent });
        return id;
    }

    public void RenameFolder(string id, string name)
    {
        requireFolder(id);
        if (id.Length == 0) throw new InvalidOperationException("The asset root cannot be renamed.");
        var folder = (JsonObject)folderMetadata[id]!;
        validateFolderName(name, (string?)folder["parent"] ?? "", id);
        Edit(_ => folder["name"] = name);
    }

    public void MoveFolder(string id, string parent)
    {
        requireFolder(id);
        requireFolder(parent);
        if (id.Length == 0) throw new InvalidOperationException("The asset root cannot be moved.");
        var ancestor = parent;
        var visited = new HashSet<string>();
        while (ancestor.Length > 0)
        {
            if (ancestor == id || !visited.Add(ancestor))
                throw new InvalidOperationException("A folder cannot be moved inside itself.");
            ancestor = (string?)folderMetadata[ancestor]?["parent"] ?? "";
        }
        validateFolderName((string)folderMetadata[id]!["name"]!, parent, id);
        Edit(_ => folderMetadata[id]!["parent"] = parent);
    }

    public void DeleteFolder(string id)
    {
        requireFolder(id);
        if (id.Length == 0 || Folders().Any(folder => folder.Parent == id) || Assets().Any(asset => asset.Folder == id))
            throw new InvalidOperationException("Only an empty asset folder can be deleted.");
        Edit(_ => folderMetadata.Remove(id));
    }

    public void MoveAsset(string group, string name, string folder)
    {
        requireFolder(folder);
        var id = AssetId(group, name) ?? throw new InvalidOperationException("That asset no longer exists.");
        Edit(_ => assetMetadata[id]!["folder"] = folder);
    }

    private void assignAssetFolder(string group, string name, string folder)
    {
        syncAssetIds();
        assetMetadata[AssetId(group, name)!]!["folder"] = folder;
    }

    public string FolderPath(string id)
    {
        var names = new List<string>();
        var visited = new HashSet<string>();
        while (id.Length > 0 && visited.Add(id) && folderMetadata[id] is { } folder)
        {
            names.Insert(0, (string)folder["name"]!);
            id = (string?)folder["parent"] ?? "";
        }
        return string.Join(" / ", names);
    }

    /// <summary>Imports artwork recursively, keeping its directory structure as browser folders. One undo takes back the batch.</summary>
    public IReadOnlyList<string> ImportDirectory(string source, string parent = "")
    {
        if (ReadOnly) throw new InvalidOperationException("Reference movies cannot import assets.");
        requireFolder(parent);
        source = System.IO.Path.GetFullPath(source);
        var files = System.IO.Directory.EnumerateFiles(source, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order(StringComparer.Ordinal).Where(file =>
                System.IO.Path.GetExtension(file).ToLowerInvariant() is ".png" or ".svg").ToArray();
        if (files.Length == 0) throw new InvalidOperationException("That folder contains no PNG or SVG artwork.");
        var names = group("textures").Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var imported = new List<(string Name, string File, string Folder)>();
        foreach (var file in files)
        {
            var basis = tidy(System.IO.Path.GetFileNameWithoutExtension(file));
            var name = basis;
            for (var number = 2; !names.Add(name); number++) name = $"{basis}_{number}";
            imported.Add((name, copyTexture(file, name, System.IO.Path.GetExtension(file).ToLowerInvariant()),
                System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(source, file)) ?? ""));
        }
        Edit(root =>
        {
            string folder(string name, string inFolder)
            {
                var existing = Folders().FirstOrDefault(each => each.Parent == inFolder && each.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) return existing.Id;
                var id = Guid.NewGuid().ToString("N");
                folderMetadata[id] = new JsonObject { ["name"] = name, ["parent"] = inFolder };
                return id;
            }
            var top = folder(new DirectoryInfo(source).Name, parent);
            if (root["textures"] is not JsonObject) root["textures"] = new JsonObject();
            foreach (var item in imported)
            {
                var destination = top;
                foreach (var segment in item.Folder.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    destination = folder(segment, destination);
                root["textures"]![item.Name] = item.File;
                assetMetadata[Guid.NewGuid().ToString("N")] = new JsonObject
                    { ["group"] = "textures", ["name"] = item.Name, ["folder"] = destination };
            }
        });
        return [.. imported.Select(item => item.Name)];
    }

    public void OpenContainingFolder(string? assetGroup = null, string? name = null)
    {
        var path = assetGroup == "textures" && name is not null && group("textures")[name] is { } texture
            ? System.IO.Path.GetDirectoryName(FilePath((string)texture!))! : Directory;
        if (ReadOnly || !System.IO.Directory.Exists(path))
            throw new InvalidOperationException("There is no local package folder to open.");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("The system could not open the containing folder.", exception);
        }
    }
}
