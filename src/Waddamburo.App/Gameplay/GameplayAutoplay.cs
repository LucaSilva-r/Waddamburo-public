using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>Diagnostic chart-driven drum input for repeatable gameplay stress runs.</summary>
internal sealed class GameplayAutoplay
{
    private readonly (TimeSpan Time, SdlKeyboardKey Key)[] _presses;
    private int _next;

    /// <remarks><paramref name="rollInterval"/>: between roll hits (default 25 ms, a stress run's 40 a second).</remarks>
    public GameplayAutoplay(IReadOnlyList<PlayableChart> charts, int singlePlayerSide, TimeSpan? rollInterval = null)
    {
        var presses = new List<(TimeSpan Time, SdlKeyboardKey Key)>();
        for (var lane = 0; lane < charts.Count; lane++)
        {
            var side = charts.Count == 1 ? singlePlayerSide : lane;
            SdlKeyboardKey key(TaikoInputAction action) => (side, action) switch
            {
                (0, TaikoInputAction.LeftDon) => SdlKeyboardKey.F,
                (0, TaikoInputAction.RightDon) => SdlKeyboardKey.J,
                (0, TaikoInputAction.LeftKa) => SdlKeyboardKey.D,
                (0, _) => SdlKeyboardKey.K,
                (_, TaikoInputAction.LeftDon) => SdlKeyboardKey.X,
                (_, TaikoInputAction.RightDon) => SdlKeyboardKey.C,
                (_, TaikoInputAction.LeftKa) => SdlKeyboardKey.Z,
                _ => SdlKeyboardKey.V,
            };
            presses.AddRange(InputsFor(charts[lane], rollInterval ?? DefaultRollInterval).Select(input => (input.Time, key(input.Action))));
        }
        _presses = [.. presses.OrderBy(press => press.Time)];
    }

    private static readonly TimeSpan DefaultRollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>The bot's drum hits for one chart, in time order: every note on time, rolls drummed every 25 ms.</summary>
    public static IReadOnlyList<TaikoReplayInput> InputsFor(PlayableChart chart) => InputsFor(chart, DefaultRollInterval);

    /// <summary>
    /// The same, rolls drummed every <paramref name="rollInterval"/>; a balloon gets its required hits only
    /// (sooner when the interval would not pop it in time), none after it bursts.
    /// </summary>
    public static IReadOnlyList<TaikoReplayInput> InputsFor(PlayableChart chart, TimeSpan rollInterval)
    {
        // ponytail: 譜面分岐 charts play the master route, where a perfect play goes at the first check;
        // a chart whose first check cannot reach master leaves the bot hitting master notes on a lower route.
        chart = chart.OnRoute(ChartBranch.Master);
        var inputs = new List<TaikoReplayInput>();
        foreach (var note in chart.HitObjects)
        {
            var ka = note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;
            inputs.Add(new(ka ? TaikoInputAction.LeftKa : TaikoInputAction.LeftDon, note.StartTime));
            if (note.IsStrong)
                inputs.Add(new(ka ? TaikoInputAction.RightKa : TaikoInputAction.RightDon, note.StartTime));
        }
        foreach (var longNote in chart.LongNotes)
        {
            // Exercise rolls and balloon effects without inserting hits close to tap notes.
            var step = rollInterval;
            var limit = int.MaxValue;
            if (longNote.IsBalloon && longNote.RequiredHits > 0)
            {
                limit = longNote.RequiredHits;
                var fits = (longNote.EndTime - longNote.StartTime - TimeSpan.FromMilliseconds(50)) / limit;
                step = TimeSpan.FromTicks(Math.Clamp(fits.Ticks, DefaultRollInterval.Ticks, rollInterval.Ticks));
            }
            var hit = longNote.StartTime + TimeSpan.FromMilliseconds(25);
            var index = 0;
            while (hit < longNote.EndTime - TimeSpan.FromMilliseconds(25) && index < limit)
            {
                if (!chart.HitObjects.Any(note => Math.Abs((note.StartTime - hit).TotalMilliseconds) < 40))
                    inputs.Add(new(index++ % 2 == 0 ? TaikoInputAction.LeftDon : TaikoInputAction.RightDon, hit));
                hit += step;
            }
        }
        return [.. inputs.OrderBy(static input => input.Time)];
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
