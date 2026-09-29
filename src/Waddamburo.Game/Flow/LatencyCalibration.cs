using Waddamburo.Catalog;

namespace Waddamburo.Game.Flow;

/// <summary>
/// The Audio Offset calibration: notes and a click on every beat play together, and the player moves the
/// notes (<see cref="Nudge"/>) until each click lands as its note reaches the mark, then confirms. The shift
/// is the Audio Offset (sound delay − display delay). Judged by eye and ear, not by tapping: tapping to clicks
/// lands early (the player anticipates the beat), and tapping to sliding notes varies too much to measure
/// (the Input Offset stays a setting).
/// </summary>
public sealed class LatencyCalibration
{
    /// <summary>100 BPM.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(600);

    /// <summary>Time before the first click: the notes scroll in, the player gets ready.</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(2.4);

    /// <remarks><paramref name="audioOffsetMs"/>: the Audio Offset it starts from (the saved one).</remarks>
    public LatencyCalibration(TimeSpan now, int audioOffsetMs = 0)
    {
        FirstClick = Beat * Math.Ceiling((now + LeadIn) / Beat);
        AudioOffsetMs = audioOffsetMs;
    }

    /// <summary>The clock time of the first click (a whole number of beats).</summary>
    public TimeSpan FirstClick { get; }

    public bool Done { get; private set; }

    /// <summary>How much later the notes are drawn than the clock.</summary>
    public int AudioOffsetMs { get; private set; }

    /// <summary>Moves the notes <paramref name="ms"/> later (negative: earlier), until done.</summary>
    public void Nudge(int ms)
    {
        if (!Done)
            AudioOffsetMs = Math.Clamp(AudioOffsetMs + ms, -500, 500);
    }

    /// <summary>The clicks and notes agree.</summary>
    public void Confirm() => Done = true;

    /// <summary>
    /// The calibration chart: a don on every beat for ten minutes, bar lines every four. Note i is on beat
    /// i + 1, so chart time zero is a beat and the click track's beats fall on the notes.
    /// </summary>
    public static PlayableChart Chart()
    {
        var length = TimeSpan.FromMinutes(10);
        var beats = (int)(length / Beat);
        var bpm = 60_000 / Beat.TotalMilliseconds;
        return new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Tja, "calibration"), "calibration"),
            TimeSpan.Zero, length,
            Enumerable.Range(0, beats - 1).Select(note => new PlayableHitObject(Beat * (note + 1), PlayableNoteKind.Don)),
            [new ChartTimingPoint(TimeSpan.Zero, bpm, 4, 4)], [new ChartScrollPoint(TimeSpan.Zero, 1)],
            [new ChartEffectPoint(TimeSpan.Zero, false)],
            Enumerable.Range(0, beats / 4).Select(bar => new ChartBarLine(Beat * (bar * 4), true)));
    }

    /// <summary>The index in <see cref="Chart"/> of the last note at or before <paramref name="time"/>; -1 before the first.</summary>
    public static int LastNoteBy(TimeSpan time) => Math.Max(-1, (int)Math.Floor(time / Beat) - 1);
}
