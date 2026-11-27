using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Waddamburo.App.Tools;

/// <summary>
/// <c>--mcp[=skin]</c>: an MCP server on stdin/stdout (JSON-RPC, one message per line) so an assistant (Claude Desktop,
/// VS Code, any MCP client) can work on a skin without a shell: read and write its files (inside the skin's folder
/// only), check it, look up the names the engine gives (Wd), and see a screen as the editor shows it.
/// </summary>
/// <remarks>ponytail: hand-rolled protocol (initialize, tools/list, tools/call, ping); the MCP SDK when resources,
/// prompts or the live editor are wanted.</remarks>
internal static class SkinMcp
{
    private const string Protocol = "2025-06-18";

    public static int Run(string root)
    {
        root = Path.GetFullPath(root.Length == 0 ? Waddamburo.Editor.SkinStage.FindRoot() : root);
        // stdout carries the protocol only: everything else the engine prints goes to stderr.
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);
        var input = new StreamReader(Console.OpenStandardInput());
        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            JsonNode? id = null;
            JsonObject reply;
            try
            {
                var message = JsonNode.Parse(line)!.AsObject();
                id = message["id"]?.DeepClone();
                if (id is null)
                    continue; // a notification (initialized, cancelled): nothing to answer
                reply = new JsonObject { ["result"] = Answer(root, message["method"]?.GetValue<string>() ?? "", message["params"] as JsonObject) };
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                reply = new JsonObject { ["error"] = new JsonObject { ["code"] = -32602, ["message"] = error.Message } };
            }
            reply["jsonrpc"] = "2.0";
            reply["id"] = id;
            output.WriteLine(reply.ToJsonString());
        }
        return 0;
    }

    internal static JsonNode Answer(string root, string method, JsonObject? arguments) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = arguments?["protocolVersion"]?.GetValue<string>() ?? Protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "waddamburo-skin", ["version"] = "0.1.0" },
            ["instructions"] = $"A Waddamburo skin at {root}. scenes.json composes the screens; each folder with a movie.json "
                + "is a package (sprites, textures, ActionScript 2 scripts that hear the engine through Wd). Check the skin "
                + "after edits; look at a screen to see the result.",
        },
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject { ["tools"] = JsonNode.Parse(ToolList) },
        "tools/call" => call(root, arguments?["name"]?.GetValue<string>() ?? "", arguments?["arguments"] as JsonObject ?? []),
        _ => throw new ArgumentException($"Unknown method {method}."),
    };

    private const string ToolList = """
        [
          {"name":"list_files","description":"The skin's files (or a folder's, relative to the skin), without the editor's caches.",
           "inputSchema":{"type":"object","properties":{"folder":{"type":"string"}}}},
          {"name":"read_file","description":"A text file of the skin (path relative to the skin: scenes.json, entry/entry/movie.json, .../scripts/main.as).",
           "inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}},
          {"name":"write_file","description":"Writes a text file of the skin (made if new). A running editor or game (--skin-dev) picks it up.",
           "inputSchema":{"type":"object","properties":{"path":{"type":"string"},"text":{"type":"string"}},"required":["path","text"]}},
          {"name":"check_skin","description":"Every problem in the skin: scripts that do not compile, Wd names the engine does not give, missing packages or textures, unused assets.",
           "inputSchema":{"type":"object","properties":{}}},
          {"name":"wd_names","description":"Every name the engine gives movies through Wd (states, lists, events, requests): kind, type, when it has a value, a sample.",
           "inputSchema":{"type":"object","properties":{}}},
          {"name":"screenshot","description":"A screen of the skin (a key of scenes.json: entry, song-select, gameplay, result...) as the skin editor shows it with example data, after some ticks (60 a second).",
           "inputSchema":{"type":"object","properties":{"screen":{"type":"string"},"ticks":{"type":"integer","default":120}},"required":["screen"]}}
        ]
        """;

    private static JsonObject call(string root, string name, JsonObject arguments)
    {
        try
        {
            return name switch
            {
                "list_files" => text(string.Join('\n', Directory.EnumerateFiles(inside(root, arguments["folder"]?.GetValue<string>() ?? ""), "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                    .Where(static file => !file.Split('/').Any(static part => part.StartsWith('.')))
                    .Order(StringComparer.Ordinal))),
                "read_file" => text(File.ReadAllText(inside(root, arguments["path"]!.GetValue<string>()))),
                "write_file" => write(root, arguments["path"]!.GetValue<string>(), arguments["text"]!.GetValue<string>()),
                "check_skin" => text(Waddamburo.Editor.SkinLint.Check(root) is { Count: > 0 } problems
                    ? string.Join('\n', problems.Select(static problem => $"{problem.File}{(problem.Line > 0 ? $":{problem.Line}:{problem.Column}" : "")}: "
                        + $"{problem.Level.ToString().ToLowerInvariant()}: {problem.Text}"))
                    : "No problems."),
                "wd_names" => text(Waddamburo.Editor.WdDump.Json()),
                "screenshot" => screenshot(root, arguments["screen"]!.GetValue<string>(), arguments["ticks"]?.GetValue<int>() ?? 120),
                _ => throw new ArgumentException($"Unknown tool {name}."),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NullReferenceException or JsonException)
        {
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = error.Message }), ["isError"] = true };
        }
    }

    private static JsonObject text(string value) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value }) };

    /// <summary>The full path of <paramref name="path"/> in the skin; refuses one outside it (.., absolute, links out).</summary>
    internal static string inside(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"{path} is outside the skin.");
        return full;
    }

    private static JsonObject write(string root, string path, string value)
    {
        var full = inside(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, value);
        return text($"Wrote {Path.GetRelativePath(root, full).Replace('\\', '/')}.");
    }

    // ponytail: a short editor run in its own window (--skin --inspect=scene:...), editor panels included; an offscreen
    // screen-only render when the panels get in the way.
    private static JsonObject screenshot(string root, string screen, int ticks)
    {
        if (!Waddamburo.Game.Scenes.SkinScenes.Screens.ContainsKey(screen))
            throw new ArgumentException($"No screen \"{screen}\": one of {string.Join(", ", Waddamburo.Game.Scenes.SkinScenes.Screens.Keys)}.");
        var shot = Path.Combine(Path.GetTempPath(), $"waddamburo-mcp-{Environment.ProcessId}.png");
        var self = Environment.ProcessPath!;
        var start = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true };
        // Run as `dotnet Waddamburo.App.dll`: the same again.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SkinMcp).Assembly.Location);
        foreach (var argument in (string[])[$"--skin={root}", $"--inspect=scene:{screen}", $"--ticks={Math.Clamp(ticks, 1, 3600)}",
                     $"--screenshot={shot}", "--no-crash-screen"])
            start.ArgumentList.Add(argument);
        File.Delete(shot);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The editor did not finish the screenshot in a minute.");
        }
        if (!File.Exists(shot))
            throw new InvalidOperationException($"No screenshot: {errors.Result.Trim()}");
        var png = File.ReadAllBytes(shot);
        File.Delete(shot);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
        };
    }
}
