using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waddamburo.Architecture.Tests;

public sealed partial class LanguageFileTests
{
    private static readonly string Languages = Path.Combine(FindRoot(), "src", "Waddamburo.App", "Languages");

    private static HashSet<string> keys(string file) => [.. strings(file).Keys];

    // String literals shaped like the English file's keys (its first segments: "menu.", "settings.", ...).
    private static HashSet<string> referenced(HashSet<string> english)
    {
        var prefixes = english.Select(static key => key[..(key.IndexOf('.') + 1)]).ToHashSet();
        return [.. Directory.EnumerateFiles(Path.Combine(FindRoot(), "src", "Waddamburo.App"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(static path => KeyLiteral().Matches(File.ReadAllText(path)).Select(static match => match.Groups[1].Value))
            .Where(literal => prefixes.Any(literal.StartsWith))];
    }

    [Fact]
    public void EveryKeyTheCodeUsesIsInEnglish()
    {
        var english = keys("en.json");
        Assert.DoesNotContain(referenced(english), key => !english.Contains(key));
    }

    [Fact]
    public void EveryEnglishKeyIsUsed()
    {
        var english = keys("en.json");
        var used = referenced(english);
        // A setting's hint is found through its label's key (key + ".hint").
        Assert.DoesNotContain(english, key => !used.Contains(key) && !(key.EndsWith(".hint", StringComparison.Ordinal) && used.Contains(key[..^5])));
    }

    [Fact]
    public void TranslationsHaveEveryEnglishKeyWithItsValues()
    {
        var english = strings("en.json");
        foreach (var file in Directory.EnumerateFiles(Languages, "*.json").Select(Path.GetFileName))
        {
            var translated = strings(file!);
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), translated.Keys.Order(StringComparer.Ordinal));
            // The same {0} {1} ... (a missing index would throw when shown).
            foreach (var (key, text) in english)
                Assert.True(indexes(text).SetEquals(indexes(translated[key])), $"{file} {key}");
        }

        static HashSet<string> indexes(string text) => [.. Placeholder().Matches(text).Select(static match => match.Groups[1].Value)];
    }

    private static Dictionary<string, string> strings(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Languages, file)))!;

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    [GeneratedRegex("\"([a-z_]+(?:\\.[a-z_]+)+)\"")]
    private static partial Regex KeyLiteral();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Waddamburo.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
