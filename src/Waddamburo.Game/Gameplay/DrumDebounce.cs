namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The player's optional debounce for a drum that sends double hits: a pad hit again within the
/// window of its last accepted hit is dropped before anything sees it. Each pad is on its own, so
/// alternating hands stay as fast as they are; one pad alone can then hit at most once a window.
/// </summary>
public sealed class DrumDebounce
{
    private readonly TimeSpan?[] _lastHit = new TimeSpan?[4];

    /// <summary>Whether the pad's hit at <paramref name="time"/> counts (a zero window accepts everything).</summary>
    public bool Accept(TaikoInputAction pad, TimeSpan time, TimeSpan window)
    {
        if (_lastHit[(int)pad] is { } last && time - last < window)
            return false;
        _lastHit[(int)pad] = time;
        return true;
    }
}
