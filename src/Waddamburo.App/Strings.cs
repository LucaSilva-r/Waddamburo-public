using System.Globalization;
using System.Text.Json;

namespace Waddamburo.App;

/// <summary>
/// The game's own text (menus, prompts, messages) in the player's language, from Languages/&lt;code&gt;.json
/// (embedded; a flat "key": "text" object, {0} {1} for the values). A key the language lacks shows in
/// English, one English lacks shows as the key.
/// </summary>
internal static class Strings
{
    private const string Prefix = "Waddamburo.Languages.";

    private static readonly Dictionary<string, string> English = load("en") ?? [];
    private static Dictionary<string, string> _chosen = English;

    /// <summary>The language in use ("en" unless another one matched).</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>
    /// Picks the language: a code ("ja", "pt-BR"), or "auto" for the system's. A regional code without its
    /// own file takes the base language's ("pt-BR" → "pt"); no match: English.
    /// </summary>
    // ponytail: set once at start; menus build their rows on first use, so a change applies after a restart.
    public static void Use(string language)
    {
        var code = language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? CultureInfo.CurrentUICulture.Name : language;
        foreach (var candidate in new[] { code, code.Split('-', '_')[0] })
            if (candidate.Length > 0 && load(candidate) is { } strings)
            {
                (_chosen, Language) = (strings, candidate);
                return;
            }
        (_chosen, Language) = (English, "en");
    }

    /// <summary>The languages there are files for ("en", "ja", ...), English first.</summary>
    public static string[] Available { get; } = [.. typeof(Strings).Assembly.GetManifestResourceNames()
        .Where(static name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(static name => name[Prefix.Length..^5]).OrderBy(static code => code != "en").ThenBy(static code => code, StringComparer.Ordinal)];

    /// <summary>A language's own name for itself ("English", "日本語").</summary>
    public static string Name(string code) => Names.GetValueOrDefault(code, code);

    private static readonly Dictionary<string, string> Names =
        Available.ToDictionary(static code => code, static code => load(code)?.GetValueOrDefault("language.name") ?? code);

    public static string T(string key) => _chosen.GetValueOrDefault(key) ?? English.GetValueOrDefault(key) ?? key;

    public static string T(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);

    private static Dictionary<string, string>? load(string code)
    {
        var assembly = typeof(Strings).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.Equals($"{Prefix}{code}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }
}
