using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// The player's gameplay layouts: &lt;modcharts&gt;/layouts/&lt;name&gt;.wddmod for one player, layouts-2p for two (applied
/// alike to both lanes), one of each active (the settings' layout and layout_2p). Waiwai has none.
/// </summary>
internal static class PlayerLayouts
{
    /// <summary>The folder of one player's (or two players') layouts; the functions below take it.</summary>
    public static string Folder(string modcharts, bool twoPlayers = false) => Path.Combine(modcharts, twoPlayers ? "layouts-2p" : "layouts");

    public static string PathOf(string folder, string name) => Path.Combine(folder, name + ChartModFile.Extension);

    /// <summary>The saved layouts' names, in name order.</summary>
    public static IReadOnlyList<string> Names(string folder) => Directory.Exists(folder)
        ? [.. Directory.EnumerateFiles(folder, "*" + ChartModFile.Extension)
            .Select(static path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.CurrentCultureIgnoreCase)]
        : [];

    /// <summary>A new empty layout ("Layout n", the first free n); its name.</summary>
    public static string Create(string folder)
    {
        var name = Enumerable.Range(1, 999).Select(static n => $"Layout {n}").First(name => !File.Exists(PathOf(folder, name)));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(PathOf(folder, name), ChartModFile.Write(new Dictionary<string, System.Collections.Immutable.ImmutableArray<ChartModKeyframe>>()));
        return name;
    }

    public static void Remove(string folder, string name)
    {
        if (File.Exists(PathOf(folder, name))) File.Delete(PathOf(folder, name));
    }

    /// <summary>
    /// Renames a layout; null when <paramref name="name"/> can't be a file name or is taken (the old one is kept).
    /// </summary>
    public static string? Rename(string folder, string from, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return null;
        if (name == from) return name;
        if (File.Exists(PathOf(folder, name))) return null;
        File.Move(PathOf(folder, from), PathOf(folder, name));
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
        var folder = Folder(modcharts);
        Directory.CreateDirectory(folder);
        var name = Enumerable.Range(1, 999).Select(static n => n == 1 ? "My layout" : $"My layout {n}")
            .First(name => !File.Exists(PathOf(folder, name)));
        File.Move(legacy, PathOf(folder, name));
        return name;
    }
}
