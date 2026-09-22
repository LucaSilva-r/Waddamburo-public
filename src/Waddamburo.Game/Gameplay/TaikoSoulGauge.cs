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

    private readonly int _great;
    private readonly int _good;
    private readonly int _miss;

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

    public int Clear { get; }
    public int Value { get; private set; }
    public int FilledSegments => Value * Segments / Max;

    public TaikoGaugeState State => Value >= Max ? TaikoGaugeState.Full
        : Value >= Clear ? TaikoGaugeState.Cleared
        : TaikoGaugeState.BelowClear;

    /// <summary>Applies one note judgement; returns whether the gauge moved.</summary>
    public bool Apply(TaikoNoteJudgement judgement)
    {
        if (judgement.StrongHitCompleted || judgement.Result is not { } result) return false;
        var previous = Value;
        Value = Math.Clamp(Value + result switch
        {
            TaikoHitResult.Great => _great,
            TaikoHitResult.Good => _good,
            _ => _miss,
        }, 0, Max);
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
