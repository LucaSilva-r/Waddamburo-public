namespace Waddamburo.Game.Flow;

/// <summary>
/// The revival drum roll after a failed first song (arcade, one player): enough hits of the player's drum before
/// time runs out and the credit goes on. The engine's rule; the screen only shows it.
/// </summary>
/// <remarks>
/// ponytail: the norma follows what we send the game's movie (40; traced 7, 100 and 40 with no visible rule) and the
/// time limit is a guess: the game's movie times the roll itself and no trace measured it. Tune both when known.
/// </remarks>
public sealed class RevivalChallenge(int norma = RevivalChallenge.DefaultNorma, int seconds = RevivalChallenge.DefaultSeconds)
{
    public const int DefaultNorma = 40;
    public const int DefaultSeconds = 10;
    private const int TicksPerSecond = 60;
    private const int LeadInTicks = 90; // the screen's own "ready" before the roll counts

    private int _ticks;

    public int Norma { get; } = norma;

    public int Hits { get; private set; }

    /// <summary>Seconds left; null before the roll starts (the lead-in) and after it ends.</summary>
    public double? TimeLeft => Succeeded is not null || _ticks <= LeadInTicks ? null
        : Math.Max(0, seconds - (double)(_ticks - LeadInTicks) / TicksPerSecond);

    /// <summary>True once the norma is reached, false once time ran out first; null while the roll runs.</summary>
    public bool? Succeeded { get; private set; }

    /// <summary>One tick, with the player's drum hits this tick.</summary>
    public void Tick(int hits)
    {
        if (Succeeded is not null)
            return;
        _ticks++;
        if (_ticks <= LeadInTicks)
            return;
        Hits = Math.Min(Norma, Hits + hits);
        if (Hits >= Norma)
            Succeeded = true;
        else if (_ticks - LeadInTicks >= seconds * TicksPerSecond)
            Succeeded = false;
    }
}
