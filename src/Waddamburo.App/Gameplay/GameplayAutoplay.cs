using Waddamburo.Catalog;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>Diagnostic chart-driven drum input for repeatable gameplay stress runs.</summary>
internal sealed class GameplayAutoplay
{
    private readonly (TimeSpan Time, SdlKeyboardKey Key)[] _presses;
    private int _next;

    public GameplayAutoplay(IReadOnlyList<PlayableChart> charts, int singlePlayerSide)
    {
        var presses = new List<(TimeSpan Time, SdlKeyboardKey Key)>();
        for (var lane = 0; lane < charts.Count; lane++)
        {
            var chart = charts[lane];
            var side = charts.Count == 1 ? singlePlayerSide : lane;
            var leftDon = side == 0 ? SdlKeyboardKey.F : SdlKeyboardKey.X;
            var rightDon = side == 0 ? SdlKeyboardKey.J : SdlKeyboardKey.C;
            var leftKa = side == 0 ? SdlKeyboardKey.D : SdlKeyboardKey.Z;
            var rightKa = side == 0 ? SdlKeyboardKey.K : SdlKeyboardKey.V;
            foreach (var note in chart.HitObjects)
            {
                var ka = note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;
                presses.Add((note.StartTime, ka ? leftKa : leftDon));
                if (note.IsStrong)
                    presses.Add((note.StartTime, ka ? rightKa : rightDon));
            }
            foreach (var longNote in chart.LongNotes)
            {
                // Exercise rolls and balloon effects without inserting hits close to tap notes.
                var hit = longNote.StartTime + TimeSpan.FromMilliseconds(25);
                var index = 0;
                while (hit < longNote.EndTime - TimeSpan.FromMilliseconds(25))
                {
                    if (!chart.HitObjects.Any(note => Math.Abs((note.StartTime - hit).TotalMilliseconds) < 40))
                        presses.Add((hit, index++ % 2 == 0 ? leftDon : rightDon));
                    hit += TimeSpan.FromMilliseconds(25);
                }
            }
        }
        _presses = [.. presses.OrderBy(press => press.Time)];
    }

    public SdlKeyboardSnapshot Apply(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        if (_next >= _presses.Length || _presses[_next].Time > chartTime)
            return keyboard;
        var presses = keyboard.Presses.ToList();
        while (_next < _presses.Length && _presses[_next].Time <= chartTime)
        {
            var press = _presses[_next++];
            var age = chartTime - press.Time;
            var timestamp = keyboard.Timestamp - age;
            presses.Add(new SdlKeyPress(press.Key, timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp));
        }
        return new SdlKeyboardSnapshot(keyboard.PressedKeys, presses, keyboard.Timestamp);
    }
}
