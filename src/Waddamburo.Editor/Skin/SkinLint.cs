using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Editor;

/// <summary>
/// Everything wrong or doubtful in a skin, for the editor's Problems tab and <c>--lint-skin</c> (CI on a skin's
/// repository): skin.json, scenes.json (as the engine reads it), each screen's packages, each package's movie.json,
/// textures and scripts (compile errors, names the engine does not give), unused assets, preview scenarios.
/// An error breaks something in the game; a warning is worth a look.
/// </summary>
public static class SkinLint
{
    public enum Level { Error, Warning }

    /// <param name="File">The file, from the skin's folder ("entry/entry/scripts/main.as").</param>
    /// <param name="Package">The package it belongs to, when one ("entry/entry"): the editor opens it.</param>
    public sealed record Problem(Level Level, string File, int Line, int Column, string Text, string? Package = null);

    public static IReadOnlyList<Problem> Check(string root)
    {
        var found = new List<Problem>();
        string relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        // skin.json: the skin's name and the contract it was made for.
        var manifest = Path.Combine(root, "skin.json");
        if (!File.Exists(manifest))
            found.Add(new(Level.Warning, "skin.json", 0, 0, "No skin.json: the skin has no name, author, version or contract."));
        else
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
                foreach (var field in (string[])["name", "author", "version", "contract"])
                    if (node[field] is null)
                        found.Add(new(Level.Warning, "skin.json", 0, 0, $"skin.json has no \"{field}\"."));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                found.Add(new(Level.Error, "skin.json", 0, 0, $"skin.json does not read: {error.Message}"));
            }
        // scenes.json, as the engine reads it; then every package a screen names.
        SkinScenes? scenes = null;
        if (File.Exists(Path.Combine(root, SkinScenes.FileName)))
            try
            {
                scenes = SkinScenes.Parse(File.ReadAllText(Path.Combine(root, SkinScenes.FileName)));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                found.Add(new(Level.Error, SkinScenes.FileName, 0, 0, $"The game ignores scenes.json: {error.Message}"));
            }
        // text.json: its fonts and styles, as the engine reads them.
        var text = SkinText.Empty;
        if (File.Exists(Path.Combine(root, SkinText.FileName)))
            try
            {
                text = SkinText.Parse(File.ReadAllText(Path.Combine(root, SkinText.FileName)), root);
                foreach (var (name, file) in text.Fonts)
                    if (!File.Exists(file))
                        found.Add(new(Level.Error, SkinText.FileName, 0, 0, $"Font \"{name}\": {relative(file)} is missing (its styles use the game's font)."));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                found.Add(new(Level.Error, SkinText.FileName, 0, 0, $"The game ignores text.json: {error.Message}"));
            }
        var packages = Directory.EnumerateFiles(root, MoviePackage.FileName, SearchOption.AllDirectories)
            .Select(file => relative(Path.GetDirectoryName(file)!)).Where(static folder => !folder.StartsWith('.')).ToHashSet(StringComparer.Ordinal);
        foreach (var key in scenes?.Keys ?? [])
            foreach (var layer in scenes!.Layers(key)!)
                if (!packages.Contains(layer.Movie))
                    found.Add(new(Level.Warning, SkinScenes.FileName, 0, 0, $"{key}: {layer.Movie} has no package (the game shows a label there).", layer.Movie));
        foreach (var package in packages.Order(StringComparer.Ordinal))
            checkPackage(root, package, found, text);
        // Preview scenarios: names the engine gives, values that read.
        var previews = Path.Combine(root, PreviewScenario.Folder);
        if (Directory.Exists(previews))
            foreach (var file in Directory.EnumerateFiles(previews, "*.json"))
            {
                var screen = Path.GetFileNameWithoutExtension(file);
                if (!SkinScenes.Screens.ContainsKey(screen))
                    found.Add(new(Level.Warning, relative(file), 0, 0, $"\"{screen}\" is no screen: these scenarios are never used."));
                foreach (var problem in PreviewScenario.Load(root, screen).Problems)
                    found.Add(new(Level.Warning, relative(file), 0, 0, problem));
            }
        return found;
    }

    private static void checkPackage(string root, string package, List<Problem> found, SkinText text)
    {
        var folder = Path.Combine(root, package);
        var movie = $"{package}/{MoviePackage.FileName}";
        MovieDocument document;
        try
        {
            document = new MovieDocument(folder);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or IOException)
        {
            found.Add(new(Level.Error, movie, 0, 0, $"movie.json does not read: {error.Message}", package));
            return;
        }
        // Assembled as the game does: what breaks it, and the scripts left out because they do not compile.
        var scriptErrors = new List<ActionScriptException>();
        try
        {
            MoviePackage.Assemble(document.Text, file => System.Text.Encoding.UTF8.GetString(document.ReadFile(file)).TrimStart('\uFEFF'), scriptErrors);
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or JsonException or IOException or KeyNotFoundException)
        {
            found.Add(new(Level.Error, movie, 0, 0, $"The movie does not assemble: {error.Message}", package));
        }
        foreach (var error in scriptErrors)
            found.Add(new(Level.Error, $"{package}/{error.File}", error.Line, error.Column, $"Does not compile (left out of the movie): {error.Problem}", package));
        foreach (var mistake in document.ProviderMistakes())
            found.Add(new(Level.Error, $"{package}/{mistake.File}", mistake.Line, mistake.Column,
                $"\"{mistake.Name}\" is no name the engine gives (Wd): the movie never hears of it.", package));
        if (document.Root["textures"] is JsonObject textures)
            foreach (var (name, path) in textures)
                if (path?.GetValue<string>() is { } file && !File.Exists(Path.Combine(folder, file)))
                    found.Add(new(Level.Error, movie, 0, 0, $"Texture \"{name}\": {file} is missing.", package));
        // Text fields: a style text.json has, a bind to a state the engine gives.
        if (document.Root["texts"] is JsonObject fields)
            foreach (var (name, field) in fields)
            {
                if (field?["style"]?.GetValueKind() == JsonValueKind.String && field["style"]!.GetValue<string>() is var style && !text.Styles.ContainsKey(style))
                    found.Add(new(Level.Warning, movie, 0, 0, $"Text \"{name}\": no style \"{style}\" in text.json (it is drawn in its own settings).", package));
                if (field?["bind"]?.GetValueKind() == JsonValueKind.String && field["bind"]!.GetValue<string>() is var bind
                    && Waddamburo.Lumen.Provider.WdNames.Find(bind) is not { Kind: Waddamburo.Lumen.Provider.WdKind.State })
                    found.Add(new(Level.Error, movie, 0, 0, $"Text \"{name}\": \"{bind}\" is no state the engine gives (Wd): the field is never filled.", package));
            }
        foreach (var (group, name) in document.Unused())
            found.Add(new(Level.Warning, movie, 0, 0, $"Unused {group[..^1]} \"{name}\".", package));
    }

    /// <summary>
    /// The skin as one zip to share (its folder, without the editor's caches and version control), beside the folder:
    /// &lt;name&gt;-&lt;version&gt;.zip. Returns its path.
    /// </summary>
    public static string Export(string root)
    {
        var name = Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
        var version = "";
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "skin.json")))!.AsObject();
            name = manifest["name"]?.GetValue<string>() ?? name;
            version = manifest["version"]?.GetValue<string>() is { } number ? "-" + number : "";
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        var safe = string.Concat(name.Select(static letter => Path.GetInvalidFileNameChars().Contains(letter) || letter == ' ' ? '-' : letter));
        var zip = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))!, $"{safe}{version}.zip");
        File.Delete(zip);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var entry = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (entry.Split('/').Any(static part => part.StartsWith('.')))
                continue;
            archive.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
        }
        return zip;
    }
}
