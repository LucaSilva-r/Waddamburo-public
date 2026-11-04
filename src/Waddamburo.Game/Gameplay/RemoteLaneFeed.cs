namespace Waddamburo.Game.Gameplay;

/// <summary>
/// Drives the other player's lane from their drum hits as they arrive over the network. The lane runs
/// <see cref="Delay"/> behind this player's own clock, so their hits, sent on time, are here before their notes reach the
/// judgement line: each lands on the note it hit, where it hit it, and the lane ends as their own game's did. The session
/// only moves as far as the other player has reported, so a late packet makes their notes wait instead of missing.
/// Hits are added from the network thread and applied on the game's.
/// </summary>
public sealed class RemoteLaneFeed
{
    private readonly Queue<TaikoReplayInput> _pending = new();
    private readonly object _lock = new();
    private TimeSpan _known = TimeSpan.MinValue;

    /// <summary>How far the lane runs behind this player's own: a little more than the time a hit takes to arrive.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How far the other player's game has reported: every hit up to it has arrived.</summary>
    public TimeSpan Known
    {
        get
        {
            lock (_lock)
                return _known;
        }
    }

    /// <summary>Their hits (in order, judgement time), and how far their game had got when it sent them.</summary>
    public void Receive(IEnumerable<TaikoReplayInput> inputs, TimeSpan knownUntil)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (_lock)
        {
            foreach (var input in inputs)
                _pending.Enqueue(input);
            if (knownUntil > _known)
                _known = knownUntil;
        }
    }

    /// <summary>Where the lane shows: this player's judgement time, minus <see cref="Delay"/>.</summary>
    public TimeSpan DisplayTime(TimeSpan localTime) => localTime - Delay;

    /// <summary>
    /// Brings <paramref name="session"/> up to the lane's time, hit by hit: <paramref name="hit"/> is told of each, for the
    /// drum's presentation. Returns the time the lane shows.
    /// </summary>
    /// <remarks><paramref name="offNote"/> is told of each hit that judged no note (a stray hit, or one on a roll or balloon).</remarks>
    public TimeSpan Apply(TaikoJudgementSession session, TimeSpan localTime, Action<TaikoInputAction, TimeSpan>? hit = null,
        Action<TaikoInputAction>? offNote = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var display = DisplayTime(localTime);
        List<TaikoReplayInput> due = [];
        TimeSpan known;
        lock (_lock)
        {
            while (_pending.TryPeek(out var next) && next.Time <= display)
                due.Add(_pending.Dequeue());
            known = _known;
        }
        foreach (var input in due)
        {
            // The other player's data: never back in time, whatever it says.
            var time = input.Time < session.CurrentTime ? session.CurrentTime : input.Time;
            hit?.Invoke(input.Action, time);
            if (session.SubmitInput(input.Action, time) is not (TaikoInputResult.Judged or TaikoInputResult.SecondHand))
                offNote?.Invoke(input.Action);
        }
        var until = known < display ? known : display;
        if (until > session.CurrentTime)
            session.AdvanceTo(until);
        return display;
    }
}
