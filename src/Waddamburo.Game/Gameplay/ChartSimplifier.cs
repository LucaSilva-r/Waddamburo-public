using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// AI battle's intermediate charts (levels 2-4, 6-8, 10-12 between かんたん 1, ふつう 5, むずかしい 9 and おに 13): the
/// course above, simplified. The game builds them from the stock charts at run time (traced: only the solo charts are
/// read); its rules are the taiko-fumen wiki's tables for Blue's 演奏バトル, which Green's mostly match: notes form runs
/// (equally spaced, by their spacing in beats: 8分 = half a beat), and each level reworks runs of some spacings
/// (one colour, triplets to the coarser spacing, long runs split 3-1 / 3-1-1, fast runs mostly gone). The levels
/// accumulate down a band: level 10 is level 12's rules, then 11's, then its own.
/// </summary>
/// <remarks>
/// ponytail: the wiki's rules for Blue; Green's per-song recipes (supportinfo.xml's ids, a second family 4-6 / 10-12
/// for a few songs) and per-song exceptions are not modelled, nor which note of a split is kept or the 4分 rule's
/// bar-line condition. Checked against plays only by note counts, later.
/// </remarks>
public static class ChartSimplifier
{
    /// <summary>The course a level is made from (its band's top), or null for a stock level (1, 5, 9, 13).</summary>
    public static TaikoCourse? SourceCourse(int level) => level switch
    {
        >= 2 and <= 4 => TaikoCourse.Normal,
        >= 6 and <= 8 => TaikoCourse.Hard,
        >= 10 and <= 12 => TaikoCourse.Oni,
        _ => null,
    };

    /// <summary>A course's level number (かんたん 1, ふつう 5, むずかしい 9, おに 13).</summary>
    public static int LevelOf(TaikoCourse course) => course switch
    {
        TaikoCourse.Easy => 1,
        TaikoCourse.Normal => 5,
        TaikoCourse.Hard => 9,
        _ => 13,
    };

    /// <summary><paramref name="chart"/> (its band's top course) simplified to <paramref name="level"/>.</summary>
    public static PlayableChart Simplify(PlayableChart chart, int level)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (SourceCourse(level) is null)
            return chart;
        var top = (level - 2) / 4 * 4 + 4; // 4, 8 or 12: the band's highest intermediate
        var notes = chart.HitObjects.ToList();
        for (var pass = top; pass >= level; pass--)
            notes = [.. notes.GroupBy(static note => note.Branch).SelectMany(route => simplify([.. route], chart, pass))
                .OrderBy(static note => note.StartTime)];
        return chart.WithHitObjects(notes);
    }

    // The note spacings the rules know, as divisions of a 4/4 bar (8 = 8分: half a beat).
    private static readonly int[] Divisions = [4, 8, 12, 16, 24, 32, 48, 64];

    private static IEnumerable<PlayableHitObject> simplify(List<PlayableHitObject> notes, PlayableChart chart, int level)
    {
        var start = 0;
        while (start < notes.Count)
        {
            // A run: notes at one spacing that is one of the divisions.
            var division = start + 1 < notes.Count ? divisionBetween(chart, notes[start], notes[start + 1]) : 0;
            var end = start + 1;
            if (division > 0)
                while (end < notes.Count && divisionBetween(chart, notes[end - 1], notes[end]) == division
                    && closeTo(notes[end].StartTime - notes[end - 1].StartTime, notes[start + 1].StartTime - notes[start].StartTime))
                    end++;
            var run = notes.GetRange(start, end - start);
            foreach (var note in division == 0 || run.Count < 2 ? run : rework(chart, run, division, level, onBeat(chart, run[0])))
                yield return note;
            start = end;
        }
    }

    // One level's rule for a run (the wiki's tables); unchanged when the table has none.
    private static IEnumerable<PlayableHitObject> rework(PlayableChart chart, List<PlayableHitObject> run, int division, int level, bool onBeat)
    {
        var n = run.Count;
        return (division, level) switch
        {
            // 4分, level 2: triplets to 2分, four to 2分 + 4分, longer runs split.
            (4, 2) => n switch { 3 => keep(run, 0, 2), 4 => keep(run, 0, 2, 3), 5 => split31(run), 6 => keep(run, 0, 1, 2, 4, 5), _ => split311(run) },
            // 8分, levels 2-3: triplets to 4分 (one colour ones; mixed ones one colour at 3), long runs split.
            (8, 2) => n switch { 3 when oneColour(run) => keep(run, 0, 2), 5 => split31(run), >= 7 => split311(run), _ => run },
            (8, 3) => n switch { 3 => oneColour(run) ? keep(run, 0, 2) : mono(run), 5 => split31(run), >= 7 => split311(run), _ => run },
            // 8分, level 6: one colour, triplets to 4分; level 7: long runs split.
            (8, 6) => n == 3 ? keep(mono(run), 0, 2) : mono(run),
            (8, 7) => n switch { 5 => split31(run), >= 7 => split311(run), _ => run },
            // 12分, levels 2-3: pairs dropped to one note, triplets to 6分, four to one colour 6分 + 12分.
            (12, 2) => n switch { 2 => keep(run, 0), 3 => keep(run, 0, 2), 4 => keep(run, 0, 2, 3), _ => split311(run) },
            (12, 3) => n switch { 2 => keep(run, 0), 3 => keep(run, 0, 2), 4 => keep(mono(run), 0, 2, 3), _ => split311(run) },
            // 12分, levels 10-12 (おに): pairs to 4分, longer runs one colour pairs; 12: four one colour, five 2+2.
            (12, 10) => n switch { 2 => keep(run, 0), 3 => keep(run, 0, 2), _ => pairs(mono(run)) },
            (12, 11) => n >= 3 ? pairs(mono(run)) : mono(run),
            (12, 12) => n switch { 4 => mono(run), 5 => keep(run, 0, 1, 3, 4), >= 7 => pairs(run), _ => run },
            // 16分, levels 6-8: triplets to 8分, five 3+1.
            (16, 6 or 7 or 8) => n switch { 3 => keep(run, 0, 2), 5 => split31(run), _ => run },
            // 16分, levels 10-12: on-beat triplets to 8分; three-four one colour, long runs split. Level 10: every
            // 16分 run to 8分 (the notes off the 8分 grid go). Traced (session-ai-support-3, mikugv おに level 10):
            // 72 of 650 notes gone, nearly all 16分 ones; the wiki's "pairs one colour" alone removes none.
            (16, 10) => run.Where(note => onEighth(chart, note)),
            (16, 11) => n == 3 && onBeat ? keep(run, 0, 2) : run,
            (16, 12) => n switch { 3 or 4 => mono(run), 5 => split31(mono(run)), >= 7 => split311(run), _ => run },
            // 24分, levels 10-11: mostly gone (five to 8分 + a 24分 pair); 12: four and more to 8分, triplets to 12分
            // on the beat (else gone), pairs to one note.
            (24, 10 or 11) => n == 5 ? keep(run, 0, 3, 4) : keep(run, 0),
            (24, 12) => n switch { 2 => keep(run, 0), 3 => onBeat ? keep(run, 0, 2) : keep(run, 0), _ => run.Where((_, i) => i % 3 == 0) },
            // 32分, levels 10-12: on-beat triplets to 16分, five to a dotted 16分 + a 32分 pair, the rest gone.
            (32, 10 or 11 or 12) => n switch { 3 when onBeat => keep(run, 0, 2), 5 => keep(run, 0, 3, 4), _ => keep(run, 0) },
            // 48分 (per song on the wiki), levels 10-12: triplets to 24分, longer runs 24分 + 48分.
            (48, 10 or 11 or 12) => n == 3 ? keep(run, 0, 2) : n >= 4 ? keep(run, 0, 2, 3) : run,
            // 64分, levels 10-12: five to a dotted 32分 + a 64分 pair.
            (64, 10 or 11 or 12) => n == 5 ? keep(run, 0, 3, 4) : run,
            _ => run,
        };
    }

    private static IEnumerable<PlayableHitObject> keep(IEnumerable<PlayableHitObject> run, params int[] indices)
    {
        var list = run.ToList();
        return indices.Where(index => index < list.Count).Select(index => list[index]);
    }

    // Five: three, a rest, one.
    private static IEnumerable<PlayableHitObject> split31(IEnumerable<PlayableHitObject> run) => keep(run, 0, 1, 2, 4);

    // Seven and more: three, then every other note (3-1-1...).
    private static IEnumerable<PlayableHitObject> split311(IEnumerable<PlayableHitObject> run) =>
        run.Where(static (_, index) => index < 3 || (index - 3) % 2 == 1);

    // Pairs with a rest between (two, rest, two...).
    private static IEnumerable<PlayableHitObject> pairs(IEnumerable<PlayableHitObject> run) => run.Where(static (_, index) => index % 3 != 2);

    private static bool oneColour(List<PlayableHitObject> run) => run.Select(static note => ka(note.Kind)).Distinct().Count() == 1;

    // The run in its first note's colour (big notes stay big; a Kadon stays as it is).
    private static List<PlayableHitObject> mono(IEnumerable<PlayableHitObject> run)
    {
        var list = run.ToList();
        var colour = ka(list[0].Kind);
        return [.. list.Select(note => note.IsKadon || ka(note.Kind) == colour ? note : recolour(note, colour))];
    }

    private static bool ka(PlayableNoteKind kind) => kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;

    private static PlayableHitObject recolour(PlayableHitObject note, bool toKa)
    {
        var big = note.Kind is PlayableNoteKind.BigDon or PlayableNoteKind.BigKa;
        var kind = (toKa, big) switch
        {
            (true, true) => PlayableNoteKind.BigKa,
            (true, false) => PlayableNoteKind.Ka,
            (false, true) => PlayableNoteKind.BigDon,
            _ => PlayableNoteKind.Don,
        };
        return new PlayableHitObject(note.StartTime, kind, note.IsHand)
        {
            Text = null, // its sung name was the other colour's
            IsSynchro = note.IsSynchro,
            InRun = note.InRun,
            IsRare = note.IsRare,
            Branch = note.Branch,
        };
    }

    // The division two notes are apart by (0: none of them, e.g. slower than 4分).
    private static int divisionBetween(PlayableChart chart, PlayableHitObject first, PlayableHitObject second)
    {
        var beats = (second.StartTime - first.StartTime).TotalMinutes * tempoAt(chart, first.StartTime).BeatsPerMinute;
        foreach (var division in Divisions)
            if (Math.Abs(beats - 4.0 / division) <= 0.04 * (4.0 / division))
                return division;
        return 0;
    }

    private static bool closeTo(TimeSpan a, TimeSpan b) => Math.Abs((a - b).TotalMilliseconds) <= Math.Max(2, 0.04 * b.TotalMilliseconds);

    // On a beat (表拍) / on the 8分 grid: a whole number of beats (half beats) from the bar line before it (the tempo
    // change's time need not be on the grid: mikugv's first bar is 4.36 beats after it).
    private static bool onBeat(PlayableChart chart, PlayableHitObject note) => onGrid(chart, note, 1);

    private static bool onEighth(PlayableChart chart, PlayableHitObject note) => onGrid(chart, note, 2);

    private static bool onGrid(PlayableChart chart, PlayableHitObject note, int perBeat)
    {
        var origin = chart.TimingPoints[0].Time;
        foreach (var bar in chart.BarLines)
        {
            if (bar.Time > note.StartTime)
                break;
            origin = bar.Time;
        }
        var units = (note.StartTime - origin).TotalMinutes * tempoAt(chart, note.StartTime).BeatsPerMinute * perBeat;
        return Math.Abs(units - Math.Round(units)) < 0.1;
    }

    private static ChartTimingPoint tempoAt(PlayableChart chart, TimeSpan time)
    {
        var current = chart.TimingPoints[0];
        foreach (var point in chart.TimingPoints)
        {
            if (point.Time > time)
                break;
            current = point;
        }
        return current;
    }
}

/// <summary>
/// The game's <c>config/*/ghost/../supportinfo.xml</c>: per song (its musicid), per course row (かんたん..裏), the recipe
/// ids of the intermediate levels below that course (0: none). AI battle's support menu offers as many steps below
/// いつもの as the row has recipes (traced SetSupportLevelNum: uhear2's rows 0, 3, 3, 2).
/// </summary>
public static class SupportInfo
{
    /// <summary>Each song's step counts below かんたん, ふつう, むずかしい and おに.</summary>
    public static IReadOnlyDictionary<string, int[]> Parse(string xml)
    {
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var root = System.Xml.Linq.XDocument.Parse(xml).Root;
        string? song = null;
        foreach (var element in root?.Elements() ?? [])
        {
            if (element.Name.LocalName == "musicid")
                song = element.Value.Trim();
            else if (element.Name.LocalName == "supportinfo" && song is not null)
            {
                var rows = element.Element("coursepatterns")?.Element("elems")?.Elements("item")
                    .Select(static row => row.Element("elems")?.Elements("item").Count(static id => id.Value.Trim() != "0") ?? 0)
                    .Take(4).ToArray() ?? [];
                counts[song] = [.. rows, .. Enumerable.Repeat(0, 4 - rows.Length)];
                song = null;
            }
        }
        return counts;
    }

    /// <summary>The installation's file (<c>data/config/&lt;cabinet&gt;/supportinfo.xml</c>), or none.</summary>
    public static IReadOnlyDictionary<string, int[]> Load(string dataRoot)
    {
        var config = Path.Combine(dataRoot, "config");
        var file = Directory.Exists(config)
            ? Directory.EnumerateFiles(config, "supportinfo.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        return file is null ? new Dictionary<string, int[]>() : Parse(File.ReadAllText(file));
    }

    /// <summary>
    /// The level the support menu's pick is (ghost_song_select's SupportInfo: a course offering n levels below it lists
    /// 1: [-2], 2: [-2, -3], 3: [-1, -2, -3] from its own, and sends the pick's place in that list, 0 = いつもの).
    /// </summary>
    public static int SupportPick(int courseLevel, int offered, int pick) =>
        pick <= 0 || offered <= 0 ? courseLevel : courseLevel - (offered >= 3 ? pick : pick + 1);
}
