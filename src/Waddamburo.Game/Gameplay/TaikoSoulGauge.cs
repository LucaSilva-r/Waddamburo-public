using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// Soul gauge: 10000 points, cleared at 60/70/80% by course, filled by per-note amounts that
/// depend on course, star band and note count. The amounts come from tja2fumen's table
/// (MIT, see Data/soul-gauge-rates.LICENSE.txt), which reproduces those of official charts.
/// </summary>
public sealed class TaikoSoulGauge
{
    public const int Max = 10000;
    /// <summary>Segments of the authored gauge movies.</summary>
    public const int Segments = 50;

    // Fractional: a branch's amounts keep their fractions (W3).
    private double _points;
    // Each route's amounts as a share of the normal route's: normal combo ÷ the route's combo.
    private readonly double[] _routeScale = [1, 1, 1];
    private readonly int _scale = 1;
    private int _great;
    private int _good;
    private int _miss;
    private readonly (TimeSpan Start, int Great, int Good, int Miss)[] _songs = [];

    public TaikoSoulGauge(TaikoCourse course, int? level, int noteCount)
    {
        Clear = course switch
        {
            TaikoCourse.Easy => 6000,
            TaikoCourse.Normal or TaikoCourse.Hard => 7000,
            _ => 8000,
        };
        (_great, _good, _miss) = rates(course, Math.Clamp(level ?? 10, 1, 10), noteCount);
    }

    /// <summary>
    /// A chart's gauge: rates from its normal route, a branched route's scaled by <see cref="PlayableChart.GaugeRatio"/>.
    /// osu! charts sit in course slots by star order only: they fill as Oni, as they are judged.
    /// </summary>
    public TaikoSoulGauge(TaikoCourse course, PlayableChart chart)
        : this(chart.Key.Song.Source == SongSourceKind.OsuLazer ? TaikoCourse.Oni : course, chart.Level,
            chart.OnRoute(ChartBranch.Normal).NoteCount)
    {
        foreach (var route in Enum.GetValues<ChartBranch>())
            _routeScale[(int)route] = chart.GaugeRatio(route);
    }

    /// <summary>
    /// A dan course's gauge: every song's amounts divided by 3, rounded towards zero (traced session30: predicts
    /// the gauge after each of 5 songs within a percent; the wiki's 達人 examples, 15 to 5 and 14 to 4, agree).
    /// </summary>
    /// <param name="songs">The course's songs.</param>
    /// <param name="exact">Nijiiro: thirds keep their fractions (the wiki's 復活！ムラサキ達人 reaches soul at 2161 combos
    /// this way, and at Green's 2408 rounded), so the gauge counts in thirds of a point.</param>
    public TaikoSoulGauge(IReadOnlyList<ChartSegment> songs, bool exact = false)
        : this(songs[0].Course, songs[0].Source.Level, songs[0].Source.NoteCount)
    {
        _scale = exact ? 3 : 1;
        _songs = [.. songs.Select(song =>
        {
            var (great, good, miss) = rates(song.Course, Math.Clamp(song.Source.Level ?? 10, 1, 10), song.Source.NoteCount);
            return exact ? (song.Start, great, good, miss) : (song.Start, great / 3, good / 3, miss / 3);
        })];
        (_great, _good, _miss) = (_songs[0].Great, _songs[0].Good, _songs[0].Miss);
    }

    public int Clear { get; }
    public int Value => (int)(_points / _scale);

    /// <summary>Sets the gauge to a point of a reviewed play.</summary>
    public void Restore(int value) => _points = Math.Clamp(value, 0, Max) * _scale;
    public int FilledSegments => Value * Segments / Max;

    public TaikoGaugeState State => Value >= Max ? TaikoGaugeState.Full
        : Value >= Clear ? TaikoGaugeState.Cleared
        : TaikoGaugeState.BelowClear;

    /// <summary>Applies one note judgement; returns whether the gauge moved.</summary>
    public bool Apply(TaikoNoteJudgement judgement)
    {
        if (judgement.StrongHitCompleted || judgement.Result is not { } result) return false;
        foreach (var song in _songs)
            if (judgement.HitObject.StartTime >= song.Start)
                (_great, _good, _miss) = (song.Great, song.Good, song.Miss);
        var previous = Value;
        _points = Math.Clamp(_points + _routeScale[(int)(judgement.HitObject.Branch ?? ChartBranch.Normal)] * (result switch
        {
            TaikoHitResult.Great => _great,
            TaikoHitResult.Good => _good,
            _ => _miss,
        }), 0, Max * _scale);
        return Value != previous;
    }

    private static (int Great, int Good, int Miss) rates(TaikoCourse course, int stars, int notes)
    {
        // Outside the table (and its all-zero last row) charts keep the fumen header defaults.
        if (notes is < 1 or >= 2500) return (10, 5, -20);
        var band = course switch
        {
            TaikoCourse.Easy => "Easy-" + (stars switch { 1 => "1", 2 or 3 => "23", _ => "45" }),
            TaikoCourse.Normal => "Normal-" + (stars switch { <= 2 => "12", 3 => "3", 4 => "4", _ => "57" }),
            TaikoCourse.Hard => "Hard-" + (stars switch { <= 2 => "12", 3 => "3", 4 => "4", _ => "58" }),
            _ => "Oni-" + (stars switch { <= 7 => "17", 8 => "8", _ => "910" }),
        };
        using var stream = typeof(TaikoSoulGauge).Assembly.GetManifestResourceStream("Waddamburo.Game.SoulGaugeRates.csv")
            ?? throw new InvalidOperationException("Soul gauge rate table is missing.");
        using var reader = new StreamReader(stream);
        var columns = reader.ReadLine()!.Split(',');
        for (var row = 1; row < notes; row++) reader.ReadLine();
        var values = reader.ReadLine()!.Split(',');
        int value(string kind) => int.Parse(values[Array.IndexOf(columns, $"{kind}_{band}")],
            System.Globalization.CultureInfo.InvariantCulture);
        return (value("good"), value("ok"), value("bad"));
    }
}
