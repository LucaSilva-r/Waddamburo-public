using System.Text;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Cli;

/// <summary>
/// --list-songs[=FILE]: every song the game lists with these settings (the libraries Song Select loads), as CSV:
/// library, id, categories, title, English title, subtitle, artist, courses (e n h m x; ★ in brackets). Libraries
/// in Song Select's order, then each library's categories in order; a song in several categories is listed once.
/// </summary>
internal static class SongList
{
    public static int Run(string dataRoot, ArcadeSettings arcade, string tjaRoot, string path)
    {
        using var libraries = SongLibraries.Load(dataRoot, arcade, tjaRoot);
        foreach (var (folder, why) in libraries.Problems)
            Console.Error.WriteLine($"Left out: {folder}: {why}");
        var snapshot = libraries.Snapshot;
        var categories = new Dictionary<SongKey, List<string>>();
        var order = new List<SongKey>();
        foreach (var category in snapshot.Categories.OrderBy(static category => SongSelectCatalogView.LibraryOrder(category.Key.Source)))
            foreach (var song in category.Songs)
            {
                if (!categories.TryGetValue(song, out var names))
                {
                    categories[song] = names = [];
                    order.Add(song);
                }
                if (!names.Contains(category.Name))
                    names.Add(category.Name);
            }
        var csv = new StringBuilder("library,id,categories,title,title_en,subtitle,artist,courses\r\n");
        foreach (var key in order)
        {
            var song = snapshot.Songs[key];
            string[] fields =
            [
                SongSelectCatalogView.LibraryName(key.Source), key.StableId, string.Join("; ", categories[key]),
                song.Title.Primary, song.Title.English ?? "", song.Subtitle ?? "", song.Artist ?? "",
                string.Join(' ', song.Charts.Where(static chart => chart.Course is not null).OrderBy(static chart => chart.Course)
                    .Select(static chart => $"{"enhmx"[(int)chart.Course!.Value]}{(chart.Level is { } level ? $"[{level}]" : "")}")),
            ];
            csv.AppendJoin(',', fields.Select(quote)).Append("\r\n");
        }
        // With a byte order mark: Excel reads the Japanese titles as UTF-8.
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"{order.Count} songs written to {path}");
        return 0;
    }

    private static string quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;
}
