using System.Text.Json.Nodes;

namespace Waddamburo.Editor;

/// <summary>
/// The package's assets as operations on movie.json (the editor's panels call these; each is one undo
/// step). Groups are the file's own: "textures", "shapes", "texts", "sprites". A refused operation throws
/// <see cref="InvalidOperationException"/> with a message for the user.
/// </summary>
public sealed partial class MovieDocument
{
    public static readonly string[] CharacterGroups = ["shapes", "texts", "sprites"];

    /// <summary>The package folder.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    private JsonObject group(string name) => Root[name] as JsonObject ?? [];

    private IEnumerable<(string Sprite, JsonObject Layer)> layers() =>
        group("sprites").SelectMany(static sprite => (sprite.Value?["layers"] as JsonArray ?? []).OfType<JsonObject>().Select(layer => (sprite.Key, layer)));

    /// <summary>Whether a name is taken: among the textures, or among the characters (which share one space).</summary>
    public bool Exists(string group, string name) =>
        group == "textures" ? this.group("textures").ContainsKey(name) : CharacterGroups.Any(each => this.group(each).ContainsKey(name));

    /// <summary>What uses an entry, as text for the user: shapes of a texture, layers of a character, the root.</summary>
    public IReadOnlyList<string> Uses(string group, string name) => group == "textures"
        ? [.. this.group("shapes").Where(shape => (string?)shape.Value?["texture"] == name).Select(static shape => $"shape {shape.Key}")]
        : [.. ((string?)Root["root"] == name ? new[] { "the movie's root" } : []),
            .. layers().Where(each => (string?)each.Layer["character"] == name)
                .Select(static each => $"{each.Sprite}: {(string?)each.Layer["name"] ?? (string?)each.Layer["character"]}")];

    /// <summary>Entries nothing uses, as (group, name).</summary>
    public IReadOnlyList<(string Group, string Name)> Unused()
    {
        string[] groups = ["textures", .. CharacterGroups];
        return [.. groups.SelectMany(each => group(each).Select(entry => (Group: each, Name: entry.Key)))
            .Where(each => Uses(each.Group, each.Name).Count == 0)];
    }

    /// <summary>A package file's full path; refuses paths that leave the package.</summary>
    public string FilePath(string relative)
    {
        if (ReadOnly)
            throw new InvalidOperationException("This movie is reference only: nothing of it can be changed or saved.");
        var root = System.IO.Path.GetFullPath(Directory);
        var path = System.IO.Path.GetFullPath(relative, root);
        return path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path
            : throw new InvalidOperationException($"'{relative}' is outside the package.");
    }

    private static string tidy(string name)
    {
        var cleaned = new string([.. name.Select(static c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]).Trim('_');
        return cleaned.Length == 0 ? "asset" : cleaned;
    }

    private string free(string group, string name)
    {
        var candidate = name;
        for (var number = 2; Exists(group, candidate); number++)
            candidate = $"{name}_{number}";
        return candidate;
    }

    /// <summary>
    /// Copies a PNG or SVG into the package's textures folder and lists it; returns the texture's name.
    /// Undo takes the entry back but leaves the copied file so redo can restore it.
    /// </summary>
    public string ImportTexture(string sourcePath, string folder = "")
    {
        requireFolder(folder);
        var extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".svg"))
            throw new InvalidOperationException("A texture is a PNG or an SVG file.");
        var name = free("textures", tidy(System.IO.Path.GetFileNameWithoutExtension(sourcePath)));
        var relative = copyTexture(sourcePath, name, extension);
        Edit(root =>
        {
            (root["textures"] as JsonObject ?? (JsonObject)(root["textures"] = new JsonObject()))[name] = relative;
            assignAssetFolder("textures", name, folder);
        });
        return name;
    }

    /// <summary>Replaces artwork under the same texture name. Keeps earlier files intact for undo and redo.</summary>
    public void ReplaceTexture(string name, string sourcePath)
    {
        if ((string?)group("textures")[name] is null)
            throw new InvalidOperationException($"There is no texture '{name}'.");
        var extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".svg"))
            throw new InvalidOperationException("A texture is a PNG or an SVG file.");
        var target = copyTexture(sourcePath, tidy(name), extension);
        Edit(root => root["textures"]![name] = target);
    }

    private string copyTexture(string sourcePath, string name, string extension)
    {
        // JSON history refers to these files: never overwrite one, including files left by undone imports.
        var relative = $"textures/{name}{extension}";
        for (var number = 2; File.Exists(FilePath(relative)); number++)
            relative = $"textures/{name}_{number}{extension}";
        var target = FilePath(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target);
        return relative;
    }

    /// <summary>Changes the artwork of a shape without changing its character or any child instance names.</summary>
    public void SetShapeTexture(string name, string? texture)
    {
        var shape = shapeNamed(name);
        if (texture is not null && !Exists("textures", texture))
            throw new InvalidOperationException($"There is no texture '{texture}'.");
        Edit(_ => { if (texture is null) shape.Remove("texture"); else shape["texture"] = texture; });
    }

    /// <summary>Sets the shape's local bounds. Layers and their animation stay in place.</summary>
    public void SetShapeRect(string name, double x, double y, double width, double height, bool continues = false)
    {
        var shape = shapeNamed(name);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
            throw new InvalidOperationException("A shape needs finite bounds with positive width and height.");
        Edit(_ => shape["rect"] = new JsonArray(x, y, width, height), continues);
    }

    /// <summary>Sets an atlas region (left, top, right, bottom in 0..1); null uses the whole texture.</summary>
    public void SetShapeUv(string name, double[]? uv, bool continues = false)
    {
        var shape = shapeNamed(name);
        validateUv(uv);
        Edit(_ => { if (uv is null) shape.Remove("uv"); else shape["uv"] = new JsonArray(uv[0], uv[1], uv[2], uv[3]); }, continues);
    }

    private JsonObject shapeNamed(string name) => group("shapes")[name] as JsonObject
        ?? throw new InvalidOperationException($"There is no shape '{name}'.");

    private static void validateUv(double[]? uv)
    {
        if (uv is not null && (uv.Length != 4 || uv.Any(static value => !double.IsFinite(value) || value < 0 || value > 1)
            || uv[0] == uv[2] || uv[1] == uv[3]))
            throw new InvalidOperationException("A texture region needs four numbers in 0..1 and a nonzero width and height.");
    }

    /// <summary>
    /// A new shape: one quad of a texture (or a host-drawn slot without one), centred on its own origin.
    /// <paramref name="uv"/>: the part of the texture as left, top, right, bottom in 0..1 (all of it when null).
    /// </summary>
    public string AddShape(string name, string? texture, double width, double height, double[]? uv = null, string folder = "")
    {
        requireFolder(folder);
        if (texture is not null && !Exists("textures", texture))
            throw new InvalidOperationException($"There is no texture '{texture}'.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("A shape needs a finite, positive size.");
        validateUv(uv);
        name = free("shapes", tidy(name));
        Edit(root =>
        {
            var shape = new JsonObject { ["rect"] = new JsonArray(-width / 2, -height / 2, width, height) };
            if (texture is not null)
                shape["texture"] = texture;
            if (uv is not null)
                shape["uv"] = new JsonArray(uv[0], uv[1], uv[2], uv[3]);
            (root["shapes"] as JsonObject ?? (JsonObject)(root["shapes"] = new JsonObject()))[name] = shape;
            assignAssetFolder("shapes", name, folder);
        });
        return name;
    }

    /// <summary>A new text field with default bounds and appearance.</summary>
    public string AddText(string name, string folder = "")
    {
        requireFolder(folder);
        name = free("texts", tidy(name));
        Edit(root =>
        {
            (root["texts"] as JsonObject ?? (JsonObject)(root["texts"] = new JsonObject()))[name] = new JsonObject
                { ["rect"] = new JsonArray(0, 0, 200, 48), ["size"] = 32, ["align"] = "left", ["color"] = "#ffffff" };
            assignAssetFolder("texts", name, folder);
        });
        return name;
    }

    /// <summary>A new, empty sprite of <paramref name="frames"/> frames.</summary>
    public string AddSprite(string name, int frames = 1, string folder = "")
    {
        requireFolder(folder);
        name = free("sprites", tidy(name));
        Edit(root =>
        {
            (root["sprites"] as JsonObject ?? (JsonObject)(root["sprites"] = new JsonObject()))[name] =
                new JsonObject { ["frames"] = Math.Clamp(frames, 1, ushort.MaxValue), ["layers"] = new JsonArray() };
            assignAssetFolder("sprites", name, folder);
        });
        return name;
    }

    /// <summary>Renames an entry and its structural package references. Keeps its identity, order, child names and script linkage.</summary>
    public void Rename(string group, string from, string to)
    {
        if (from == to)
            return;
        if (to.Length == 0 || to != tidy(to))
            throw new InvalidOperationException("A name is letters, digits and underscores.");
        if (!this.group(group).ContainsKey(from))
            throw new InvalidOperationException($"There is no '{from}' in {group}.");
        if (Exists(group, to))
            throw new InvalidOperationException($"The name '{to}' is taken.");
        Edit(root =>
        {
            if (AssetId(group, from) is { } identity)
                assetMetadata[identity]!["name"] = to;
            var entries = (JsonObject)root[group]!;
            var pairs = entries.ToList();
            entries.Clear();
            foreach (var (key, value) in pairs)
                entries[key == from ? to : key] = value;
            if (group == "textures")
            {
                foreach (var shape in this.group("shapes"))
                    if ((string?)shape.Value?["texture"] == from)
                        shape.Value!["texture"] = to;
                return;
            }
            if ((string?)root["root"] == from)
                root["root"] = to;
            foreach (var (_, layer) in layers())
                if ((string?)layer["character"] == from)
                    layer["character"] = to;
        });
    }

    /// <summary>Removes an entry nothing uses (its texture file stays on disk, so undo is whole).</summary>
    public void Delete(string group, string name)
    {
        if (Uses(group, name) is { Count: > 0 } users)
            throw new InvalidOperationException($"'{name}' is used by {string.Join(", ", users)}.");
        Edit(root => (root[group] as JsonObject)?.Remove(name));
    }

}
