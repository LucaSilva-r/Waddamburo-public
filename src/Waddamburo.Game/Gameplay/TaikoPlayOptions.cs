using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>The random option: some (きまぐれ, 20%) or many (でたらめ, 50%) notes swap colour.</summary>
public enum TaikoRandom
{
    None,
    Kimagure,
    Detarame,
}

/// <summary>
/// A player's play options (演奏オプション), chosen on Song Select's course boards: 真打 scoring, the notes'
/// speed (one of <see cref="Speeds"/>), ドロン (notes not drawn, only their text), あべこべ (don and ka
/// swapped) and random.
/// </summary>
/// <remarks>
/// Rules from the taiko-fumen wiki (システム/基本システム, 配点), AC15 Green; the speeds are Nijiiro's (Green has
/// x2-x4 only, whose art stands for them by range: <see cref="SpeedIcon"/>). Song Select reports the rest as
/// bits, one per chosen item id (<see cref="FromSession"/>); a play keeps <see cref="Bits"/>.
/// </remarks>
public readonly record struct TaikoPlayOptions(bool Shinuchi, int SpeedIndex, bool Doron, bool Abekobe, TaikoRandom Random)
{
    /// <summary>Nijiiro's speeds: 1.0 to 2.0 by 0.1, then to 4.0 by 0.5.</summary>
    public static readonly double[] Speeds = [1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0, 2.5, 3.0, 3.5, 4.0];

    /// <summary>No options (also the default value).</summary>
    public static TaikoPlayOptions None => default;

    /// <summary>The notes' scroll multiplier.</summary>
    public double ScrollSpeed => Speeds[Math.Clamp(SpeedIndex, 0, Speeds.Length - 1)];

    /// <summary>Green's speed art standing for it: 1 none, 2 ばいそく (up to 2.0), 3 さんばい (to 3.0), 4 よんばい.</summary>
    public int SpeedIcon => ScrollSpeed switch
    {
        <= 1 => 1,
        <= 2 => 2,
        <= 3 => 3,
        _ => 4,
    };

    /// <summary>A speed's label, e.g. 1.3x.</summary>
    public static string SpeedText(int index) =>
        $"{Speeds[Math.Clamp(index, 0, Speeds.Length - 1)].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x";

    /// <summary>Anything chosen.</summary>
    public bool Any => SessionBits != 0;

    /// <summary>
    /// Options from song_select's GetSession: bit (1 &lt;&lt; item id) for each row's choice. Item ids
    /// (SessionResource): 0 none, 1 真打, 2-4 speed x2-x4, 5 ドロン, 6 あべこべ, 7 きまぐれ, 8 でたらめ.
    /// <paramref name="speedIndex"/>: the exact speed (the speed bits are only its art's range).
    /// </summary>
    public static TaikoPlayOptions FromSession(int bits, int? speedIndex = null)
    {
        static bool has(int bits, int item) => (bits & (1 << item)) != 0;
        var bucket = has(bits, 4) ? 14 : has(bits, 3) ? 12 : has(bits, 2) ? 10 : 0; // 4.0, 3.0, 2.0
        return new(has(bits, 1), speedIndex ?? bucket, has(bits, 5), has(bits, 6),
            has(bits, 8) ? TaikoRandom.Detarame : has(bits, 7) ? TaikoRandom.Kimagure : TaikoRandom.None);
    }

    /// <summary>A play's kept options: <see cref="SessionBits"/> with the speed's index from bit 9.</summary>
    public int Bits => SessionBits | (Math.Clamp(SpeedIndex, 0, Speeds.Length - 1) << 9);

    public static TaikoPlayOptions FromBits(int bits) => FromSession(bits & 0x1FF, bits >> 9 is > 0 and var index ? index : null);

    /// <summary>The options as <see cref="FromSession"/> reads them (no bit for a row left at none).</summary>
    public int SessionBits => (Shinuchi ? 1 << 1 : 0) | (SpeedIcon > 1 ? 1 << SpeedIcon : 0) | (Doron ? 1 << 5 : 0)
        | (Abekobe ? 1 << 6 : 0) | Random switch
        {
            TaikoRandom.Kimagure => 1 << 7,
            TaikoRandom.Detarame => 1 << 8,
            _ => 0,
        };

    /// <summary>
    /// The chart as played with these options: faster scrolling, don and ka swapped (あべこべ, then each
    /// note again by the random option's chance, from <paramref name="seed"/> so a replay gets the same
    /// notes) and, with 真打, kusudama played as balloons and the shin-uchi base set.
    /// </summary>
    public PlayableChart Apply(PlayableChart chart, int seed)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (!Any)
            return chart;
        var random = new System.Random(seed);
        var chance = Random switch
        {
            TaikoRandom.Kimagure => 0.2,
            TaikoRandom.Detarame => 0.5,
            _ => 0,
        };
        var abekobe = Abekobe;
        var notes = chart.HitObjects.Select(note => abekobe ^ (chance > 0 && random.NextDouble() < chance) ? swapped(note) : note).ToArray();
        var longNotes = Shinuchi
            ? chart.LongNotes.Select(static note => note.Kind == PlayableLongNoteKind.Kusudama
                ? new PlayableLongNote(note.StartTime, note.EndTime, PlayableLongNoteKind.Balloon, note.RequiredHits) : note)
            : chart.LongNotes;
        var speed = ScrollSpeed;
        var played = new PlayableChart(chart.Key, chart.AuthoredOffset, chart.Duration, notes, chart.TimingPoints,
            chart.ScrollPoints.Select(point => new ChartScrollPoint(point.Time, point.Multiplier * speed)),
            chart.EffectPoints, chart.BarLines, longNotes)
        {
            Level = chart.Level,
            ScoreInit = chart.ScoreInit,
            ScoreDiff = chart.ScoreDiff,
        };
        return Shinuchi ? played with { ShinuchiBase = ShinuchiBase(played) } : played;
    }

    /// <summary>
    /// A chart's 真打 base: the all-Great ceiling, (notes + big notes) × base plus each balloon's (hits − 1)
    /// × 300 + 5000, comes to about 1,000,000 (the wiki's rule; Green keeps no per-chart value we know of).
    /// </summary>
    /// <remarks>ponytail: rounded up to ten; the wiki's per-chart values may round differently.</remarks>
    public static int ShinuchiBase(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var units = chart.NoteCount + chart.HitObjects.Count(static note => note.IsStrong);
        if (units == 0)
            return 10;
        var balloons = chart.LongNotes.Where(static note => note.IsBalloon)
            .Sum(static note => (long)(note.RequiredHits - 1) * 300 + 5000);
        var budget = Math.Max(0, 1_000_000 - balloons);
        return (int)Math.Max(10, (budget + units * 10 - 1) / (units * 10) * 10);
    }

    private static PlayableHitObject swapped(PlayableHitObject note) =>
        new(note.StartTime, note.Kind switch
        {
            PlayableNoteKind.Don => PlayableNoteKind.Ka,
            PlayableNoteKind.Ka => PlayableNoteKind.Don,
            PlayableNoteKind.BigDon => PlayableNoteKind.BigKa,
            _ => PlayableNoteKind.BigDon,
        }, note.IsHand)
        {
            IsSynchro = note.IsSynchro,
            InRun = note.InRun,
            IsKo = false, // a ko is a don's run name
            IsRare = note.IsRare,
        };
}
