using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// The player's gameplay layouts: &lt;modcharts&gt;/layouts/&lt;name&gt;.wddmod, one of them active (the settings' layout).
/// </summary>
internal static class PlayerLayouts
{
    public static string Folder(string modcharts) => Path.Combine(modcharts, "layouts");

    public static string PathOf(string modcharts, string name) => Path.Combine(Folder(modcharts), name + ChartModFile.Extension);

    /// <summary>The saved layouts' names, in name order.</summary>
    public static IReadOnlyList<string> Names(string modcharts) => Directory.Exists(Folder(modcharts))
        ? [.. Directory.EnumerateFiles(Folder(modcharts), "*" + ChartModFile.Extension)
            .Select(static path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.CurrentCultureIgnoreCase)]
        : [];

    /// <summary>A new empty layout ("Layout n", the first free n); its name.</summary>
    public static string Create(string modcharts)
    {
        var name = Enumerable.Range(1, 999).Select(static n => $"Layout {n}").First(name => !File.Exists(PathOf(modcharts, name)));
        Directory.CreateDirectory(Folder(modcharts));
        File.WriteAllBytes(PathOf(modcharts, name), ChartModFile.Write(new Dictionary<string, System.Collections.Immutable.ImmutableArray<ChartModKeyframe>>()));
        return name;
    }

    public static void Remove(string modcharts, string name)
    {
        if (File.Exists(PathOf(modcharts, name))) File.Delete(PathOf(modcharts, name));
    }

    /// <summary>
    /// Renames a layout; null when <paramref name="name"/> can't be a file name or is taken (the old one is kept).
    /// </summary>
    public static string? Rename(string modcharts, string from, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return null;
        if (name == from) return name;
        if (File.Exists(PathOf(modcharts, name))) return null;
        File.Move(PathOf(modcharts, from), PathOf(modcharts, name));
        return name;
    }

    /// <summary>
    /// The single layout of earlier versions (modcharts/layout.wddmod) moved in as "My layout"; its name, or null when
    /// there was none.
    /// </summary>
    public static string? Migrate(string modcharts)
    {
        var legacy = Path.Combine(modcharts, "layout" + ChartModFile.Extension);
        if (!File.Exists(legacy)) return null;
        Directory.CreateDirectory(Folder(modcharts));
        var name = Enumerable.Range(1, 999).Select(static n => n == 1 ? "My layout" : $"My layout {n}")
            .First(name => !File.Exists(PathOf(modcharts, name)));
        File.Move(legacy, PathOf(modcharts, name));
        return name;
    }
}
