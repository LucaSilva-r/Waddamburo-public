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
    /// ponytail: undo takes the entry back but leaves the copied file.
    /// </summary>
    public string ImportTexture(string sourcePath)
    {
        var extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".svg"))
            throw new InvalidOperationException("A texture is a PNG or an SVG file.");
        var name = free("textures", tidy(System.IO.Path.GetFileNameWithoutExtension(sourcePath)));
        var relative = $"textures/{name}{extension}";
        var target = FilePath(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (System.IO.Path.GetFullPath(sourcePath) != target)
            File.Copy(sourcePath, target, overwrite: true);
        Edit(root => (root["textures"] as JsonObject ?? (JsonObject)(root["textures"] = new JsonObject()))[name] = relative);
        return name;
    }

    /// <summary>Puts another file's content in a texture's place (same name, so every shape using it follows).</summary>
    public void ReplaceTexture(string name, string sourcePath)
    {
        if ((string?)group("textures")[name] is not { } relative)
            throw new InvalidOperationException($"There is no texture '{name}'.");
        var extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".svg"))
            throw new InvalidOperationException("A texture is a PNG or an SVG file.");
        var target = System.IO.Path.ChangeExtension(relative, extension);
        File.Copy(sourcePath, FilePath(target), overwrite: true);
        if (target != relative)
            Edit(root => root["textures"]![name] = target);
        else
            Changed?.Invoke(); // Only the file changed: the movie still has to load it.
    }

    /// <summary>
    /// A new shape: one quad of a texture (or a host-drawn slot without one), centred on its own origin.
    /// <paramref name="uv"/>: the part of the texture as left, top, right, bottom in 0..1 (all of it when null).
    /// </summary>
    public string AddShape(string name, string? texture, double width, double height, double[]? uv = null)
    {
        if (texture is not null && !Exists("textures", texture))
            throw new InvalidOperationException($"There is no texture '{texture}'.");
        if (!(width > 0) || !(height > 0) || uv is { Length: not 4 })
            throw new InvalidOperationException("A shape needs a size, and four numbers for a region.");
        name = free("shapes", tidy(name));
        Edit(root =>
        {
            var shape = new JsonObject { ["rect"] = new JsonArray(-width / 2, -height / 2, width, height) };
            if (texture is not null)
                shape["texture"] = texture;
            if (uv is not null)
                shape["uv"] = new JsonArray(uv[0], uv[1], uv[2], uv[3]);
            (root["shapes"] as JsonObject ?? (JsonObject)(root["shapes"] = new JsonObject()))[name] = shape;
        });
        return name;
    }

    /// <summary>A new, empty sprite of <paramref name="frames"/> frames.</summary>
    public string AddSprite(string name, int frames = 1)
    {
        name = free("sprites", tidy(name));
        Edit(root => (root["sprites"] as JsonObject ?? (JsonObject)(root["sprites"] = new JsonObject()))[name] =
            new JsonObject { ["frames"] = Math.Clamp(frames, 1, ushort.MaxValue), ["layers"] = new JsonArray() });
        return name;
    }

    /// <summary>Renames an entry and everything that names it. The entry keeps its place in the file.</summary>
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
