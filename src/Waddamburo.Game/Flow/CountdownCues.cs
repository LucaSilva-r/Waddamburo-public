namespace Waddamburo.Game.Flow;

/// <summary>
/// The game's countdown sounds, the same for every menu timer (entry, Song Select, the entry's card
/// dialog; traced session3-countdown, session14-card-entry): VO_COM cue 1 at 30 s left, cue 2 at 10,
/// cue 3 at 5, and the SE_COM tick (cue 13) every second from 10 down to 0.
/// </summary>
public sealed class CountdownCues
{
    private int? _last;

    /// <summary>A new timer: its first reading sets the start without a sound.</summary>
    public void Reset() => _last = null;

    /// <summary>The cues for the whole seconds passed since the last reading, in play order.</summary>
    public IReadOnlyList<(string Bank, int Cue)> Advance(int secondsLeft)
    {
        var cues = new List<(string, int)>();
        if (_last is { } last)
            for (var second = last - 1; second >= Math.Max(secondsLeft, 0); second--)
            {
                if (second == 30) cues.Add(("VO_COM", 1));
                if (second == 10) cues.Add(("VO_COM", 2));
                if (second == 5) cues.Add(("VO_COM", 3));
                if (second <= 10) cues.Add(("SE_COM", 13));
            }
        _last = secondsLeft;
        return cues;
    }
}
