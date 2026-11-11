using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Waddamburo.Editor;

/// <summary>A script of a package and where it runs: a class file, or a frame (number or label) of a sprite.</summary>
public sealed record MovieScript(string File, string? Sprite, string? Frame)
{
    public bool IsClass => Sprite is null;
}

public sealed partial class MovieDocument
{
    // ------------------------------------------------------------------------------------------- scripts

    /// <summary>Every script the movie runs, classes first.</summary>
    public IReadOnlyList<MovieScript> Scripts() =>
        [.. (Root["classes"] as JsonArray ?? []).Select(static file => new MovieScript((string?)file ?? "", null, null)),
            .. group("sprites").SelectMany(static sprite => (sprite.Value?["scripts"] as JsonObject ?? [])
                .Select(script => new MovieScript((string?)script.Value ?? "", sprite.Key, script.Key)))];

    private void ensureScript(string file, string comment)
    {
        if (!file.EndsWith(".as", StringComparison.Ordinal))
            throw new InvalidOperationException("A script file ends in .as.");
        var path = FilePath(file);
        if (File.Exists(path))
            return;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"// {comment}\n");
    }

    /// <summary>
    /// Runs a script file when a sprite enters a frame (a number, or one of the sprite's labels), in place of the
    /// one there. The file is made, with a comment, when it does not exist.
    /// </summary>
    public void AttachScript(string sprite, string frame, string file)
    {
        if (group("sprites")[sprite] is not JsonObject entry)
            throw new InvalidOperationException($"There is no sprite '{sprite}'.");
        if (!(int.TryParse(frame, out var number) ? number >= 0 && number < ((int?)entry["frames"] ?? 1)
            : (entry["labels"] as JsonObject)?.ContainsKey(frame) == true))
            throw new InvalidOperationException($"'{frame}' is no frame or label of '{sprite}'.");
        ensureScript(file, $"Runs when '{sprite}' enters frame {frame}.");
        Edit(_ => (entry["scripts"] as JsonObject ?? (JsonObject)(entry["scripts"] = new JsonObject()))[frame] = file);
    }

    /// <summary>Stops running the script on a sprite's frame (the file stays).</summary>
    public void DetachScript(string sprite, string frame) => Edit(_ =>
    {
        if (group("sprites")[sprite]?["scripts"] is not JsonObject scripts)
            return;
        scripts.Remove(frame);
        if (scripts.Count == 0)
            ((JsonObject)group("sprites")[sprite]!).Remove("scripts");
    });

    /// <summary>Adds a class file: run once before anything is placed. Made, with a comment, when it does not exist.</summary>
    public void AddClass(string file)
    {
        ensureScript(file, "Classes: runs once before the movie's first frame.");
        if (Scripts().Any(script => script.IsClass && script.File == file))
            return;
        Edit(root => (root["classes"] as JsonArray ?? (JsonArray)(root["classes"] = new JsonArray())).Add((JsonNode)file));
    }

    public void RemoveClass(string file) => Edit(root =>
    {
        if (root["classes"] is not JsonArray classes)
            return;
        foreach (var each in classes.Where(each => (string?)each == file).ToArray())
            classes.Remove(each);
        if (classes.Count == 0)
            root.Remove("classes");
    });

    /// <summary>
    /// Opens a package file in the program the system uses for it (scripts are edited outside: the editor reloads
    /// the movie when the file is saved). False when the system could not.
    /// </summary>
    public void OpenInCode(string relative, int line = 1, int column = 1)
    {
        var start = new ProcessStartInfo("code") { UseShellExecute = false };
        start.ArgumentList.Add("--reuse-window");
        start.ArgumentList.Add("--goto");
        start.ArgumentList.Add($"{FilePath(relative)}:{Math.Max(1, line)}:{Math.Max(1, column)}");
        using var process = Process.Start(start);
    }

    public bool OpenOutside(string relative)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(FilePath(relative)) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
