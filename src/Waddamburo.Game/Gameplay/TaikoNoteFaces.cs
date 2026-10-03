using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The notes' faces follow the combo. Traced (session1, 18, 20, 26): the game sends every note on
/// screen to <c>level01</c> .. <c>level04</c> by the lane's combo, the next label once the combo passes
/// 50, 150 and 300 (combo 50 still sent level01, 51 level02), whatever Go-Go; a miss drops back to
/// level01. It re-sends the label every quarter beat. The face movies (onp_don and kin, read from the
/// assets): level01 a still closed mouth; level02 a 40-frame closed/open loop; level03 and level04
/// 20-frame loops (level04 with the fierce face), all switching on 10-frame steps. Beat-synced movies
/// run about 40 frames a beat, so a tier is shown here at its beat phase: level02 opens once a beat,
/// level03/04 twice.
/// </summary>
public static class TaikoNoteFaces
{
    /// <summary>The authored frames of one beat (a level02 loop).</summary>
    private const int FramesPerBeat = 40;

    public static string Label(int combo) =>
        combo > 300 ? "level04" : combo > 150 ? "level03" : combo > 50 ? "level02" : "level01";

    /// <summary>
    /// Long notes (rolls, balloons): only jumps to the tier's label when it changes and lets the movie
    /// loop it. ponytail: no beat phase; seeking a roll every tick rebuilt its stretched tail (flicker).
    /// </summary>
    public static void ShowTier(LumenPlayer note, int combo)
    {
        ArgumentNullException.ThrowIfNull(note);
        var label = Label(combo);
        if (!note.Labels.ContainsKey(label))
            return;
        var current = note.Labels.Where(pair => pair.Value <= note.CurrentFrame).MaxBy(pair => pair.Value).Key;
        if (current != label)
            note.TryGotoLabel("", label);
    }

    /// <summary>Puts a note movie on its tier's frame for <paramref name="beat"/> (beats since the chart's first timing point).</summary>
    public static void Show(LumenPlayer note, int combo, double beat)
    {
        ArgumentNullException.ThrowIfNull(note);
        var label = Label(combo);
        if (!note.Labels.TryGetValue(label, out var start) && !note.Labels.TryGetValue(label = "level01", out start))
            return;
        var frame = start;
        if (label != "level01")
        {
            var end = note.Labels.Values.Where(value => value > start).DefaultIfEmpty(note.FrameCount).Min();
            var length = end - start;
            frame += (int)Math.Floor(((beat * FramesPerBeat % length) + length) % length);
        }
        if (note.CurrentFrame != frame && (uint)frame < (uint)note.FrameCount)
            note.GotoFrame(frame, play: false);
    }
}
