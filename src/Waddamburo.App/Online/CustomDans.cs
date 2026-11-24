using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Online;

/// <summary>
/// The player's own dan courses: one series per JSON file in the TJA library's <c>Dani</c> folder (created when missing,
/// with an example and a README). A song is a chart's hash (any library) or a TJA path relative to the TJA library with its
/// course. They are unranked: their course hashes start with <see cref="Prefix"/> and never upload, but their bests
/// are kept locally like any course's. Format: docs/development/custom-tja.md ("Custom dan dojo") and the example file.
/// </summary>
internal static class CustomDans
{
    /// <summary>Every custom course hash and series id starts with this.</summary>
    public const string Prefix = "custom:";

    public const string FolderName = "Dani";

    /// <summary>The series in <paramref name="tjaRoot"/>/Dani, their songs matched against the library.</summary>
    public static async Task<List<DanSeries>> LoadAsync(string? tjaRoot, SongCatalogSnapshot snapshot, ChartHashes hashes,
        Func<ChartKey, CatalogAssetKey, CancellationToken, ValueTask<PlayableChart>> loadChart)
    {
        if (tjaRoot is null || !Directory.Exists(tjaRoot))
            return [];
        var folder = Path.Combine(tjaRoot, FolderName);
        try
        {
            Directory.CreateDirectory(folder);
            // Written every start, so they follow the game's version (the player's own .json files are never touched).
            File.WriteAllText(Path.Combine(folder, ExampleName), Example);
            File.WriteAllText(Path.Combine(folder, ReadmeName), Readme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Custom dan: could not create {folder}: {exception.Message}");
            return [];
        }
        var files = Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return [];
        var library = new Library(tjaRoot, snapshot, hashes, loadChart);
        var series = new List<DanSeries>();
        foreach (var file in files)
        {
            try
            {
                series.Add(await readAsync(file, library).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Console.WriteLine($"Custom dan: {Path.GetFileName(file)} skipped: {exception.Message}");
            }
        }
        Console.WriteLine($"Custom dan: {series.Count} series, {series.Sum(static entry => entry.Courses.Count)} courses, "
            + $"{series.SelectMany(static entry => entry.Courses).SelectMany(static course => course.Songs).Count(static song => song.Charts.Count == 0)} songs not found.");
        return series;
    }

    private static async Task<DanSeries> readAsync(string file, Library library)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var id = Prefix + Path.GetFileNameWithoutExtension(file);
        var courses = new List<DanCourse>();
        var level = 0;
        foreach (var course in root.GetProperty("courses").EnumerateArray())
        {
            var songs = new List<DanCourseSong>();
            foreach (var song in course.GetProperty("songs").EnumerateArray())
                songs.Add(await library.SongAsync(song).ConfigureAwait(false));
            var requirements = course.TryGetProperty("requirements", out var listed)
                ? listed.EnumerateArray().Select(requirement => new DanRequirement(
                    requirement.GetProperty("type").GetString() ?? throw new InvalidDataException("A requirement has no type."),
                    values(requirement, "red", songs.Count), values(requirement, "gold", songs.Count))).ToList()
                : [];
            var name = course.GetProperty("name").GetString() ?? throw new InvalidDataException("A course has no name.");
            // Edited songs or requirements make a new course (its bests start over), as the server's hashes do.
            var hash = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{id}\n{course.GetRawText()}\n{string.Join(',', songs.SelectMany(static song => song.Charts))}"))).ToLowerInvariant();
            courses.Add(new DanCourse(++level, name, hash, requirements, songs,
                course.TryGetProperty("rules", out var rules) ? rules.GetString() : null));
        }
        return new DanSeries(id, root.TryGetProperty("name", out var title) && title.GetString() is { Length: > 0 } named
            ? named : Path.GetFileNameWithoutExtension(file), courses);
    }

    // red/gold: one number for the whole course, or one per song (0 = none for that song).
    private static int[] values(JsonElement requirement, string name, int songs)
    {
        if (!requirement.TryGetProperty(name, out var value))
            return [0];
        if (value.ValueKind == JsonValueKind.Number)
            return [value.GetInt32()];
        var each = value.EnumerateArray().Select(static entry => entry.GetInt32()).ToArray();
        return each.Length == songs ? each
            : throw new InvalidDataException($"'{name}' lists {each.Length} values for {songs} songs.");
    }

    // The library's charts by TJA path and by hash, as the course files name them.
    private sealed class Library(string tjaRoot, SongCatalogSnapshot snapshot, ChartHashes hashes,
        Func<ChartKey, CatalogAssetKey, CancellationToken, ValueTask<PlayableChart>> loadChart)
    {
        private Dictionary<string, (SongDescriptor Song, SongChartDescriptor Chart)>? _byKey;
        private bool _hashedLibrary;

        public async Task<DanCourseSong> SongAsync(JsonElement entry)
        {
            var title = entry.TryGetProperty("title", out var named) ? named.GetString() : null;
            var found = entry.TryGetProperty("hash", out var hash)
                ? await byHashAsync(hash.GetString()!.Trim().ToLowerInvariant()).ConfigureAwait(false)
                : byPath(entry.GetProperty("path").GetString()!, course(entry));
            if (found is not var (song, chart))
                return new DanCourseSong("", entry.TryGetProperty("hash", out _) ? 3 : course(entry), title ?? "?", 0, []);
            var sha = await hashes.HashAsync(chart).ConfigureAwait(false) ?? "";
            var playable = await loadChart(chart.Key, chart.ChartAsset, CancellationToken.None).ConfigureAwait(false);
            // A branched chart's combo goes through its master branch.
            var notes = playable.HitObjects.Count(static note => note.Branch is null or ChartBranch.Master);
            return new DanCourseSong(song.Key.StableId, (int)chart.Course!.Value, title ?? song.Title.Primary, notes, [sha]);
        }

        private static int course(JsonElement entry) => (entry.TryGetProperty("course", out var value) ? value.GetString() : "oni")
            ?.ToLowerInvariant() switch
        {
            "easy" or "kantan" => 0,
            "normal" or "futsuu" => 1,
            "hard" or "muzukashii" => 2,
            "oni" => 3,
            "ura" or "edit" or "uraoni" => 4,
            var other => throw new InvalidDataException($"Unknown course '{other}' (easy, normal, hard, oni or ura)."),
        };

        private (SongDescriptor, SongChartDescriptor)? byPath(string path, int course)
        {
            var wanted = Path.GetFullPath(Path.Combine(tjaRoot, path));
            foreach (var song in snapshot.Songs.Values.Where(static song => song.Key.Source == SongSourceKind.Tja))
                foreach (var chart in song.Charts.Where(chart => (int?)chart.Course == course))
                    if (TjaCatalogProvider.RelativeChartPath(chart.ChartAsset) is { } relative
                        && string.Equals(Path.GetFullPath(Path.Combine(tjaRoot, relative)), wanted, StringComparison.OrdinalIgnoreCase))
                        return (song, chart);
            Console.WriteLine($"Custom dan: no {path} ({course}) in the TJA library.");
            return null;
        }

        private async Task<(SongDescriptor, SongChartDescriptor)?> byHashAsync(string sha)
        {
            _byKey ??= snapshot.Songs.Values.SelectMany(static song => song.Charts.Select(chart => (song, chart)))
                .GroupBy(static entry => entry.chart.Key.ToString()).ToDictionary(static group => group.Key, static group => group.First());
            foreach (var key in hashes.KeysOf(sha))
                if (_byKey.TryGetValue(key, out var found))
                    return found;
            // Not hashed yet (offline, a first start): the whole library once, then again.
            if (!_hashedLibrary)
            {
                _hashedLibrary = true;
                await hashes.HashLibraryInBackground(snapshot.Songs.Values).ConfigureAwait(false);
                return await byHashAsync(sha).ConfigureAwait(false);
            }
            Console.WriteLine($"Custom dan: no chart with hash {sha} in the library.");
            return null;
        }
    }

    private const string ExampleName = "Example.json.txt";

    // A copy renamed to .json becomes a series (its songs are placeholders).
    private const string Example = """
        // A custom dan dojo: copy this file, name the copy <anything>.json, and it appears in the Dan Dojo folder (unranked:
        // never uploaded, best results kept on this computer). One file is one series; its courses in order.
        // A song is either a TJA path relative to the TJA library with its course (easy, normal, hard, oni, ura),
        // or the hash of any chart in your library (in Song Select, H copies the song under the cursor's hashes).
        // Everything else (every field, requirement types, hashes): README.txt beside this file.
        // Course names that are dan ranks (五段, 十段, 達人...) use that rank's art; any other name is a challenge board.
        // Requirements: soul_gauge, goods, oks, bads, combo, hits, score, rolls. red and gold are one value for the whole
        // course, or a list with one value per song. bads and oks must stay under the value, the rest reach it.
        {
          "name": "My Dojo",
          "courses": [
            {
              "name": "五段",
              "songs": [
                { "path": "Vocaloid/Senbonzakura/Senbonzakura.tja", "course": "oni" },
                { "path": "Pop/Some Song/Some Song.tja", "course": "hard" },
                { "hash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
              ],
              "requirements": [
                { "type": "soul_gauge", "red": 98, "gold": 100 },
                { "type": "goods", "red": [600, 700, 800], "gold": [700, 800, 900] },
                { "type": "bads", "red": 40, "gold": 20 }
              ]
            }
          ]
        }
        """;

    private const string ReadmeName = "README.txt";

    private const string Readme = """
        CUSTOM DAN DOJO
        ===============

        Every .json file in this folder is one dan dojo series of your own. It appears in Song Select's Dan Dojo
        folder after the official series, named after its "name". Changes are read when the game starts.

        Custom courses are unranked: they are never uploaded to TaikOnline and earn no Don Medals. Your results are
        kept on this computer and show on the course boards (red or gold pass) like any course's.

        Start from Example.json.txt: copy it, rename the copy to something.json, edit it. JSON comments (// ...) and
        trailing commas are allowed. A file the game cannot read is skipped; the game log (waddamburo/logs/latest.log)
        says why, on lines starting with "Custom dan:".


        THE FILE
        --------

        {
          "name": "My Dojo",                  series name (optional; the file name otherwise)
          "courses": [                        the courses, in the order they are listed
            {
              "name": "五段",                  course name (see COURSE NAMES)
              "songs": [ ... ],               the songs, played in this order (see SONGS)
              "requirements": [ ... ],        what passing asks (see REQUIREMENTS)
              "rules": "nijiiro"              optional: Nijiiro's soul gauge instead of Green's
            }
          ]
        }


        COURSE NAMES
        ------------

        A course named after a dan rank uses that rank's board and art:
          初級, 十級 九級 八級 七級 六級 五級 四級 三級 二級 一級,
          初段, 二段 三段 四段 五段 六段 七段 八段 九段 十段, 玄人, 名人, 超人, 達人
        Any other name ("Speed Run", "Challenge 1"...) is a challenge course: its name is written on the challenge
        plates instead.


        SONGS
        -----

        Each song is one of:

          { "path": "Vocaloid/Senbonzakura/Senbonzakura.tja", "course": "oni" }
              A TJA chart of your TJA library: its path from the TJA library's folder (this folder's parent), and
              its course: easy, normal, hard, oni or ura (oni when left out).

          { "hash": "3f6c...e91a" }
              Any chart of any of your libraries (official songs, TJA, osu!, Wii...), by its chart hash (see HASHES).
              The hash names one course of one chart, so no "course" is needed.

        Either may add "title": "..." : the name shown while the chart is missing from your library.

        A course whose songs are not all in your library is shown locked.


        HASHES
        ------

        A chart hash is Waddamburo's chart identity, the same one TaikOnline uses for scores: the SHA-256 of the
        chart's playable content for one course (its notes and their timing, rolls, balloons, BPM and scroll changes,
        go-go time, bar lines, branches, its level and scoring values). It is not the hash of the file: renaming, moving or re-tagging a file keeps it,
        and the same chart in two formats or libraries has the same hash. Changing anything about how it plays
        gives a new hash.

        To get one: in Song Select, put the cursor on a song (or open its course select) and press H. Its charts are
        copied to the clipboard, one line per course, ready to paste into "songs":

          { "hash": "3f6c...e91a", "title": "Senbonzakura (Oni)" },

        Keep the line of the course you want. The first time a file names a hash the game has not seen, it works out
        the whole library's hashes once (in the background; a large library takes a while), then shows the course.


        REQUIREMENTS
        ------------

        { "type": "goods", "red": 900, "gold": 1000 }

        "red" is what passing asks, "gold" what a gold pass asks. Each is either one number for the whole course, or a
        list with one number per song ("red": [300, 300, 400]): then each song must meet its own number on its own,
        and a song that misses ends the course after it. 0 means no requirement (for that song, or at that level).

          type          counts                                        passing means
          soul_gauge    the soul gauge at the end, in percent          at least the value
          goods         良 (good) hits                                 at least the value
          oks           可 (ok) hits                                   fewer than the value
          bads          不可 (bad) hits and misses                      fewer than the value
          combo         the longest combo                              at least the value
          hits          良 + 可 + roll hits (たたけた数)                  at least the value
          score         the score                                      at least the value
          rolls         roll and balloon hits                          at least the value

        oks and bads fail as soon as they reach their value; goods and combo fail once the notes left cannot reach
        it. The course then ends after the current song. The others are judged at the end.

        The course panels have room for three requirements. With more, the soul gauge is left off the panel (the
        gauge bar shows it anyway); every requirement still counts.


        EDITING
        -------

        A course's results belong to its exact content: changing its songs or requirements makes it a new course,
        and its board starts again with no result.
        """;
}
