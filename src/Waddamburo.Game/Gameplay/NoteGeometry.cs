using Waddamburo.Catalog;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>A note's place on the stage: its transform (position, turn, size) and opacity.</summary>
public readonly record struct NotePlacement(LumenMatrix Transform, float Alpha);

/// <summary>
/// Where notes and bar lines are on the 1280x720 stage, and when they are on the lane: functions of chart time alone.
/// A plain chart scrolls right to left at its tempo and scroll; the chart's gimmicks bend that: complex and backwards
/// scroll, #DIRECTION, #SUDDEN, scrolling by beat, the target's #JPOSSCROLL moves and the note mods of modcharts.
/// </summary>
public sealed class NoteGeometry
{
    private readonly PlayableChart _chart;
    private readonly ChartEffects _effects;
    private readonly ChartModValues _mods;
    private readonly bool _noteMods;

    public NoteGeometry(PlayableChart chart, float hitX, float hitY, ChartModValues mods)
    {
        _chart = chart;
        _effects = chart.Effects;
        _mods = mods;
        HitX = hitX;
        HitY = hitY;
        _noteMods = mods.Touches("notes");
        WideView = mods.Touches("playfield") || mods.Touches("camera");
    }

    /// <summary>The hit target's centre before any #JPOSSCROLL move.</summary>
    public float HitX { get; }
    public float HitY { get; }

    /// <summary>A moved camera or playfield can bring the lane's far ends into view: notes are kept further out.</summary>
    public bool WideView { get; }

    /// <summary>How far the target has moved (#JPOSSCROLL) at <paramref name="time"/>.</summary>
    public (float X, float Y) JudgeOffset(TimeSpan time)
    {
        if (_effects.JudgeMoves.IsEmpty) return (0, 0);
        var (x, y) = _effects.JudgeOffsetAt(time);
        return ((float)x, (float)y);
    }

    /// <summary>The target's centre at <paramref name="time"/>.</summary>
    public (float X, float Y) Origin(TimeSpan time)
    {
        var (x, y) = JudgeOffset(time);
        return (HitX + x, HitY + y);
    }

    /// <summary>
    /// Where something due at <paramref name="noteTime"/> is at <paramref name="time"/>, moving by its scroll point at
    /// <paramref name="controlTime"/>. <paramref name="hitNote"/>: #SUDDEN's wait holds it (OpenTaiko: hit notes only).
    /// </summary>
    public (float X, float Y) Point(TimeSpan noteTime, TimeSpan time, TimeSpan controlTime, ChartBranch? route, bool hitNote = false)
    {
        var (ox, oy) = Origin(time);
        var scroll = _chart.ScrollPointAt(controlTime, route);
        var now = hitNote && scroll.Wait is { } wait && noteTime - time > wait ? noteTime - wait : time;
        // #HBSCROLL/#BMSCROLL: by the beats left (four to 948 px), else by the seconds at the note's tempo.
        // ponytail: the beat count is the main route's; a 譜面分岐 route with its own tempo scrolls by it too.
        var distance = scroll.ByBeat ? (_effects.BeatAt(noteTime) - _effects.BeatAt(now)) * 948 / 4
            : (noteTime - now).TotalSeconds * ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(controlTime, route), 1);
        var (x, y) = (ox + distance * scroll.Multiplier, oy + distance * scroll.Imaginary);
        if (_noteMods)
            (x, y) = noteMods(x, y, distance, noteTime, time);
        return ((float)Math.Clamp(x, -100_000, 100_000), (float)Math.Clamp(y, -100_000, 100_000));
    }

    /// <summary>
    /// A note, its text (<paramref name="yOffset"/> below it) or a bar line at <paramref name="time"/>; null when it is
    /// hidden (#SUDDEN) or off the stage. <paramref name="hitNote"/>: a hit note or its text (#SUDDEN and note mods
    /// apply); <paramref name="barLine"/>: it stands across the way it travels (complex scroll), notes stay upright.
    /// </summary>
    public NotePlacement? Place(TimeSpan noteTime, TimeSpan time, ChartBranch? route, float yOffset = 0, bool hitNote = false, bool barLine = false)
    {
        var scroll = _chart.ScrollPointAt(noteTime, route);
        if (hitNote && scroll.Show is { } show && noteTime - time > show)
            return null;
        var (x, y) = Point(noteTime, time, noteTime, route, hitNote);
        y += yOffset;
        var margin = WideView ? 1500 : 100;
        if (x < -margin || x > 1280 + margin || y < -margin || y > 720 + margin)
            return null;
        var (turn, size, alpha) = (0d, 1f, 1f);
        if (barLine && scroll.Imaginary != 0)
            turn = Math.Atan2(scroll.Imaginary, scroll.Multiplier);
        // Note mods on the note itself: dizzy turns it by how far it is, zoom sizes it, alpha fades it.
        if (hitNote && _noteMods)
            (turn, size, alpha) = (_mods.Value("notes", "dizzy", time) * (x - HitX) / 100,
                (float)_mods.Value("notes", "zoom", time), Math.Clamp((float)_mods.Value("notes", "alpha", time), 0, 1));
        var (cos, sin) = ((float)Math.Cos(turn), (float)Math.Sin(turn));
        return new NotePlacement(new LumenMatrix(size * cos, size * sin, -size * sin, size * cos, x, y), alpha);
    }

    /// <summary>When a note due at <paramref name="noteTime"/> can be on the lane (so a frame skips the rest with one comparison).</summary>
    public (TimeSpan From, TimeSpan Until) Window(TimeSpan noteTime, ChartBranch? route)
    {
        var scroll = _chart.ScrollPointAt(noteTime, route);
        var speed = ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(noteTime, route), Math.Sqrt(scroll.Multiplier * scroll.Multiplier + scroll.Imaginary * scroll.Imaginary));
        // A moved camera or playfield: listed as long as any of it could be on screen (a turned or shrunk lane).
        if (WideView && !scroll.ByBeat && speed >= 1 && scroll.Show is null && scroll.Wait is null)
            return (noteTime - TimeSpan.FromSeconds(4000 / speed), noteTime + TimeSpan.FromSeconds(4000 / speed));
        if (scroll.ByBeat && !_effects.Beats.IsEmpty)
        {
            // Within a screen's diagonal of the target, in beats; a #DELAY on the way keeps it listed longer.
            var beat = _effects.BeatAt(noteTime);
            var reachBeats = 1500 / (948 / 4.0 * Math.Max(0.01, Math.Sqrt(scroll.Multiplier * scroll.Multiplier + scroll.Imaginary * scroll.Imaginary)));
            var from = _effects.TimeAtBeat(beat - reachBeats);
            if (scroll.Show is { } appear && noteTime - appear > from)
                from = noteTime - appear;
            return (from, _effects.TimeAtBeat(beat + reachBeats) + TimeSpan.FromSeconds(1));
        }
        // #SUDDEN: shown from its appear time (a note held still may wait on screen from the start).
        if (scroll.Show is not null || scroll.Wait is not null)
            return (scroll.Show is { } show ? noteTime - show : TimeSpan.MinValue, noteTime + TimeSpan.FromSeconds(1500 / Math.Max(1, speed)));
        if (scroll.Multiplier > 0 && scroll.Imaginary == 0 && _effects.JudgeMoves.IsEmpty)
            return VisibleWindow(noteTime, pixelsPerSecond(noteTime, route), HitX);
        // Backwards or slanted scroll, a moving target: from any side, within a screen's diagonal of the target.
        var reach = TimeSpan.FromSeconds(1500 / speed);
        return speed < 1 ? (TimeSpan.MinValue, TimeSpan.MaxValue) : (noteTime - reach, noteTime + reach);
    }

    /// <summary>A bar line follows whichever route is playing, so it stays listed for as long as the slowest would show it.</summary>
    public (TimeSpan From, TimeSpan Until) BarWindow(TimeSpan time) => _chart.ScrollPointAt(time).ByBeat || WideView
        ? Window(time, null)
        : VisibleWindow(time, Enum.GetValues<ChartBranch>().Min(route => pixelsPerSecond(time, route)), HitX);

    private double pixelsPerSecond(TimeSpan controlTime, ChartBranch? route) =>
        ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(controlTime, route), _chart.ScrollAt(controlTime, route));

    /// <summary>
    /// Scroll velocity on the 1280x720 stage: four beats at scroll 1 span 948 pixels,
    /// including the offscreen approach. The hit target's position does not set the speed.
    /// </summary>
    // Public reference and independent runtime measurements: docs/development/gameplay-integration.md.
    public static double ScrollPixelsPerSecond(double beatsPerMinute, double scrollMultiplier) =>
        beatsPerMinute / 240 * 948 * scrollMultiplier;

    /// <summary>When something moving at this speed is between x -100 and 1380, the lane's drawn range; always, if it barely moves.</summary>
    public static (TimeSpan From, TimeSpan Until) VisibleWindow(TimeSpan noteTime, double pixelsPerSecond, float hitX) =>
        pixelsPerSecond < 1
            ? (TimeSpan.MinValue, TimeSpan.MaxValue)
            : (noteTime - TimeSpan.FromSeconds((1380 - hitX) / pixelsPerSecond), noteTime + TimeSpan.FromSeconds((hitX + 100) / pixelsPerSecond));

    // NotITG-style note mods, by screen axis: drunk and beat across the lane, tipsy and wave along it.
    private (double X, double Y) noteMods(double x, double y, double distance, TimeSpan noteTime, TimeSpan time)
    {
        var seconds = time.TotalSeconds;
        var drunk = _mods.Value("notes", "drunk", time);
        if (drunk != 0) y += drunk * 30 * Math.Cos(seconds * 2.5 + distance / 120);
        var tipsy = _mods.Value("notes", "tipsy", time);
        if (tipsy != 0) x += tipsy * 25 * Math.Cos(seconds * 5 + noteTime.TotalSeconds * 4);
        var wave = _mods.Value("notes", "wave", time);
        if (wave != 0) x += wave * 20 * Math.Sin(distance / 38);
        var beat = _mods.Value("notes", "beat", time);
        if (beat != 0)
        {
            // A bump on every beat that fades over the first fifth of it.
            var count = _effects.Beats.IsEmpty ? seconds * _chart.BeatsPerMinuteAt(time) / 60 : _effects.BeatAt(time);
            var phase = count - Math.Floor(count);
            if (phase < 0.2)
                y += beat * 20 * (1 - phase / 0.2) * Math.Sin(distance / 15 + Math.PI / 2);
        }
        return (x, y);
    }
}
