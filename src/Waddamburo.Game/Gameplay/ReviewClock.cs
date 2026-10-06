namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The chart clock of a reviewed play (a replay, the autoplay): it runs at <see cref="Speed"/> times
/// real time, pauses, and seeks anywhere between <see cref="Start"/> and <see cref="End"/>. The caller
/// advances it with the real time that passed each frame.
/// </summary>
public sealed class ReviewClock(TimeSpan start, TimeSpan end)
{
    /// <summary>The speeds <see cref="Faster"/> and <see cref="Slower"/> step through.</summary>
    public static readonly double[] Speeds = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2];

    public TimeSpan Start { get; } = start;

    public TimeSpan End { get; } = end;

    public TimeSpan Position { get; private set; } = start;

    public double Speed { get; private set; } = 1;

    public bool Paused { get; private set; }

    /// <summary>Raised when the position jumps (a seek), with the old and the new position.</summary>
    public event Action<TimeSpan, TimeSpan>? Sought;

    /// <summary>Song seconds per real second at the last update (negative: scrubbing back; 0: standing still).</summary>
    public double Rate { get; private set; }

    /// <summary>Whether the last update moved by scrubbing (held seek keys) rather than playing.</summary>
    public bool Scrubbing { get; private set; }

    private TimeSpan _scrubHeld;

    /// <summary>
    /// Runs on by <paramref name="realElapsed"/> times the speed (stopping at the end), or, while a seek
    /// key is held (<paramref name="scrub"/> -1 back, +1 forward), scrubs at a pace that ramps up the longer
    /// it is held; letting go stops the scrub at once and the play carries on as it was (paused or not).
    /// </summary>
    public void Update(TimeSpan realElapsed, int scrub = 0)
    {
        if (realElapsed <= TimeSpan.Zero)
            return;
        var before = Position;
        Scrubbing = scrub != 0;
        if (Scrubbing)
        {
            Position = clamp(Position + realElapsed * (Math.Sign(scrub) * ScrubPace(_scrubHeld)));
            _scrubHeld += realElapsed;
        }
        else
        {
            _scrubHeld = TimeSpan.Zero;
            if (!Paused)
            {
                Position = clamp(Position + realElapsed * Speed);
                if (Position == End)
                    Paused = true;
            }
        }
        Rate = (Position - before) / realElapsed;
    }

    /// <summary>Song seconds per real second after holding a seek key this long: 2, doubling every 0.6 s, up to 40.</summary>
    public static double ScrubPace(TimeSpan held) => Math.Min(40, 2 * Math.Pow(2, held.TotalSeconds / 0.6));

    public void Seek(TimeSpan position)
    {
        var previous = Position;
        Position = clamp(position);
        if (Position != previous)
            Sought?.Invoke(previous, Position);
    }

    public void SeekBy(TimeSpan delta) => Seek(Position + delta);

    public void TogglePause()
    {
        // Playing again from the end starts over.
        if (Paused && Position == End)
            Seek(Start);
        Paused = !Paused;
    }

    public void SetPaused(bool paused) => Paused = paused;

    public void Faster() => step(+1);

    public void Slower() => step(-1);

    private void step(int direction)
    {
        var index = Array.FindIndex(Speeds, speed => speed >= Speed);
        Speed = Speeds[Math.Clamp((index < 0 ? Speeds.Length - 1 : index) + direction, 0, Speeds.Length - 1)];
    }

    private TimeSpan clamp(TimeSpan position) => position < Start ? Start : position > End ? End : position;
}
