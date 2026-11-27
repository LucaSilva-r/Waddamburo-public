using System.Text.Json;
using Waddamburo.Lumen.Provider;

namespace Waddamburo.Editor;

/// <summary>
/// Every name the engine gives movies (<see cref="WdNames"/>) as JSON: tools/vscode-waddamburo/wd-names.json, which the
/// VS Code extension completes and explains from (<c>--dump-wd=file</c> writes it; a test keeps the copy current).
/// </summary>
public static class WdDump
{
    public static string Json() => JsonSerializer.Serialize(new
    {
        version = WdNames.Version,
        names = WdNames.All.Select(static name => new
        {
            name = name.Name,
            kind = name.Kind.ToString().ToLowerInvariant(),
            type = name.Type,
            scope = name.Scope,
            description = name.Description,
            sample = name.Sample,
        }),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
