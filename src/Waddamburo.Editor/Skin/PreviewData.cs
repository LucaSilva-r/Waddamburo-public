using System.Runtime.CompilerServices;
using Waddamburo.Game.Library;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// Example data for the editor's previews, given through the same provider the game uses: every state at a plausible
/// value, the lists filled (ranks, dan requirements, drum sounds, a course's songs), both drums joined with names, and
/// for the list screens (Song Select, the dan list) the engine's own <see cref="LibraryRows"/> over an example library,
/// taking the movie's row requests as the game does. ponytail: one fixed set; named scenarios per screen come later.
/// </summary>
internal static class PreviewData
{
    private static readonly ConditionalWeakTable<LumenPlayer, Action> Ticks = [];

    // States that are there only for a moment (a drumroll, a balloon under way): left out, they would cover the screen.
    private static readonly HashSet<string> Momentary = ["play.roll", "play.balloon", "play.kusudama", "revival.result", "card.player"];

    /// <summary>Gives the movie its example data (once, when <paramref name="fresh"/>); the action to run every tick, if it needs one.</summary>
    public static Action? Attach(LumenPlayer player, string role, bool fresh, int? side = null)
    {
        if (!fresh)
            return Ticks.TryGetValue(player, out var kept) ? kept : null;
        player.RequestHandler = static (_, _) => null; // taken quietly: a preview has no game to act on them
        foreach (var name in WdNames.All)
            if (name.Kind == WdKind.State && name.Sample.Length > 0 && !Momentary.Contains(name.Name))
                foreach (var drum in (int[])[0, 1, 2])
                    player.Publish(name.Name, MovieInspectorSession.ParseValue(name.Sample), drum);
        player.Publish("player.name", LumenHostValue.FromString("Don-chan"), 1);
        player.Publish("player.name", LumenHostValue.FromString("Katsu-chan"), 2);
        player.Publish("player.side", LumenHostValue.FromNumber(1), 2);
        // A layer kept to one drum (a name board) belongs to that drum.
        if (side is { } own)
        {
            player.Publish("player.name", LumenHostValue.FromString(own == 0 ? "Don-chan" : "Katsu-chan"));
            player.Publish("player.side", LumenHostValue.FromNumber(own));
        }
        list(player, "scores", ("name", ["Don-chan", "Katsu-chan", "Wada-don"]), ("score", [1002340, 987650, 954320]));
        list(player, "tone", ("name", ["Taiko", "Festival", "Dogs & Cats", "Deluxe Taiko", "Drum Kit", "Tambourine"]));
        list(player, "dan.exam", ("type", ["soul_gauge", "goods", "bads"]), ("target", [100, 900, 10]), ("gold", [100, 950, 5]),
            ("value", [88, 640, 3]), ("possible", [true, true, true]));
        list(player, "dan.song", ("Title", ["Senbonzakura", "Yoru ni Kakeru", "Kurenai"]), ("Good", [301, 412, 520]),
            ("Ok", [40, 22, 61]), ("Bad", [5, 2, 9]));
        list(player, "ai", ("won", [true, false, true]));
        list(player, "result", ("rareHit", [true, true, false]));
        Action? tick = role switch
        {
            "song-select" => library(player, songs()),
            "dani-select" => library(player, courses()),
            _ => null,
        };
        if (tick is not null)
            Ticks.AddOrUpdate(player, tick);
        return tick;
    }

    // Lists by prefix: "scores" + ("name", [...]) publishes scores.name[i] and scores.count. dan.song's names are
    // dan.songTitle..., so a field may also be glued on (capitalised).
    private static void list(LumenPlayer player, string prefix, params (string Field, object[] Values)[] fields)
    {
        foreach (var (field, values) in fields)
            for (var index = 0; index < values.Length; index++)
                player.PublishItem(char.IsUpper(field[0]) ? prefix + field : $"{prefix}.{field}", index, values[index] switch
                {
                    string text => LumenHostValue.FromString(text),
                    bool flag => LumenHostValue.FromBoolean(flag),
                    var number => LumenHostValue.FromNumber(Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture)),
                });
        if (WdNames.Find(prefix + ".count") is not null)
            player.Publish(prefix + ".count", LumenHostValue.FromNumber(fields[0].Values.Length));
    }

    private static Action library(LumenPlayer player, IReadOnlyList<LibraryNode> top)
    {
        var rows = new LibraryRows(top);
        // Opened on the third folder, the cursor on its first song: a list with some depth to it.
        rows.Open(Math.Min(2, rows.Rows.Count - 1));
        rows.Select(Math.Min(3, rows.Rows.Count - 1));
        static int at(LumenHostValue[] arguments, int index) =>
            arguments.Length > index && arguments[index].Kind == LumenHostValueKind.Number ? (int)arguments[index].AsNumber() : -1;
        player.RequestHandler = (name, arguments) => name switch
        {
            "rows.window" => rows.Window(at(arguments, 0), at(arguments, 1)),
            "select.row" => rows.Select(at(arguments, 0)),
            "row.open" => rows.Open(at(arguments, 0)),
            "row.close" => rows.Close(at(arguments, 0)),
            _ => null,
        };
        return () => rows.Publish(player);
    }

    private static LibraryNode song(string title, string artist, params int[] stars)
    {
        var node = new LibraryNode(LibraryNodeKind.Song, title) { Subtitle = artist, Artist = artist };
        string[] names = ["easy", "normal", "hard", "oni", "ura"];
        for (var course = 0; course < stars.Length; course++)
            node.Children.Add(new LibraryNode(LibraryNodeKind.Chart, names[course]) { Stars = stars[course], Course = names[course] });
        return node;
    }

    private static LibraryNode folder(string name, params LibraryNode[] songs)
    {
        var node = new LibraryNode(LibraryNodeKind.Folder, name);
        node.Children.AddRange(songs);
        return node;
    }

    private static IReadOnlyList<LibraryNode> songs() =>
    [
        folder("Pop", song("Yoru ni Kakeru", "YOASOBI", 3, 5, 6, 8), song("Pretender", "Official HIGE DANdism", 2, 4, 6, 7),
            song("Kaikai Kitan", "Eve", 3, 5, 7, 9)),
        folder("Anime", song("Gurenge", "LiSA", 3, 5, 7, 8), song("Unravel", "TK from Ling tosite sigure", 4, 6, 7, 9)),
        folder("Vocaloid", song("Senbonzakura", "Kurousa-P", 3, 5, 7, 9, 10), song("Ghost Rule", "DECO*27", 3, 5, 7, 9),
            song("Melt", "ryo (supercell)", 2, 4, 6, 8)),
        folder("Namco Original", song("Saitama 2000", "Taiko Team", 4, 6, 8, 10), song("Hataraku Dosukoi", "Taiko Team", 3, 5, 7, 9),
            song("Ekiben 2000", "Taiko Team", 5, 7, 9, 10, 10)),
        folder("Game Music", song("Megalovania", "Toby Fox", 3, 5, 7, 9)),
    ];

    private static IReadOnlyList<LibraryNode> courses()
    {
        LibraryNode course(string name, params string[] songs)
        {
            var node = new LibraryNode(LibraryNodeKind.Course, name);
            node.Children.AddRange(songs.Select(static title => new LibraryNode(LibraryNodeKind.Chart, title) { Stars = 8, Course = "oni" }));
            return node;
        }
        return
        [
            course("五段", "Yoru ni Kakeru", "Gurenge", "Melt"), course("六段", "Senbonzakura", "Kaikai Kitan", "Saitama 2000"),
            course("七段", "Ghost Rule", "Unravel", "Hataraku Dosukoi"), course("名人", "Ekiben 2000", "Megalovania", "Senbonzakura"),
        ];
    }
}
