using System.Diagnostics;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Flow;

/// <summary>
/// One showing of the entry (player join), as the engine keeps it whatever movie shows it: the countdown, who joined
/// (paid with the coin bank), the card waiting for a drum, and the home setup's hold on the drums. A skin's entry
/// reads it and follows its events; the entry flow and the movies' requests change it.
/// </summary>
public sealed class EntrySession
{
    private readonly Stopwatch _timer = new();
    private TimeSpan _limit;
    private readonly HashSet<int> _joined = [];

    /// <summary>The cabinet's credits in coin mode; null in free play.</summary>
    public CoinBank? Coins { get; init; }

    /// <summary>Whether a player may join, on either drum (the right one is P2); both may.</summary>
    public Func<int, bool> CanJoin { get; init; } = static _ => true;

    /// <summary>The home player setup is open over the entry: it has the drums, joins only raise the stands.</summary>
    public bool SetupMode { get; set; }

    /// <summary>A card being read or waiting for a drum (from the attract loop or read during entry).</summary>
    public ScoreProfile? Card { get; set; }

    /// <summary>A drum joined (in the setup too, where it only raises its stand).</summary>
    public event Action<int>? Joined;

    /// <summary>A player is in for real: a join outside the setup, or one the setup confirmed.</summary>
    public event Action<int>? Entered;

    /// <summary>A drum took the waiting card.</summary>
    public event Action<int, ScoreProfile>? CardClaimed;

    /// <summary>The countdown was set (it runs from <see cref="ResumeCountdown"/>).</summary>
    public event Action<double>? CountdownStarted;

    /// <summary>The countdown runs again.</summary>
    public event Action? CountdownResumed;

    public bool IsJoined(int side) => _joined.Contains(side);

    public int JoinedCount => _joined.Count;

    /// <summary>The first joined player's drum (0 left, 1 right); null before a join.</summary>
    public int? FirstJoined => _joined.Count == 0 ? null : _joined.First();

    /// <summary>Free play, or the credits cover the next player.</summary>
    public bool CanPay => Coins is null || Coins.Missing(_joined.Count) == 0;

    /// <summary>Pays (coin mode) and joins the drum; false when it is in already, may not join or cannot pay.</summary>
    public bool TryJoin(int side)
    {
        if (side is not (0 or 1) || _joined.Contains(side) || !CanJoin(side) || Coins?.TryJoin(_joined.Count) == false)
            return false;
        _joined.Add(side);
        Joined?.Invoke(side);
        if (!SetupMode)
            Entered?.Invoke(side);
        return true;
    }

    /// <summary>A drum already in when the entry starts (back from Song Select for the other drum's join).</summary>
    public void PreJoin(int side) => _joined.Add(side);

    /// <summary>The setup confirmed this drum's player.</summary>
    public void Enter(int side) => Entered?.Invoke(side);

    /// <summary>The setup released this drum.</summary>
    public void Leave(int side) => _joined.Remove(side);

    /// <summary>The drum takes the waiting card; false when none waits.</summary>
    public bool ClaimCard(int side)
    {
        if (Card is not { } card)
            return false;
        Card = null;
        CardClaimed?.Invoke(side, card);
        return true;
    }

    public bool CountdownSet { get; private set; }

    /// <summary>Sets the countdown to its full time, stopped.</summary>
    public void StartCountdown(double seconds)
    {
        _limit = TimeSpan.FromSeconds(seconds);
        CountdownSet = true;
        _timer.Reset();
        CountdownStarted?.Invoke(seconds);
    }

    public void ResumeCountdown()
    {
        _timer.Start();
        CountdownResumed?.Invoke();
    }

    public void PauseCountdown() => _timer.Stop();

    /// <summary>Remaining countdown seconds (whole, rounded up); the full time while not running.</summary>
    public int RemainingSeconds => (int)Math.Ceiling(Math.Max(0, (_limit - _timer.Elapsed).TotalSeconds));

    /// <summary>The countdown's seconds left; null while none is set.</summary>
    public int? TimeLeft => CountdownSet ? RemainingSeconds : null;

    public bool TimeUp => CountdownSet && RemainingSeconds == 0;
}
