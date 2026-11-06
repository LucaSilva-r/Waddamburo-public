using System.Collections.Immutable;
using Waddamburo.Catalog;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Authored presentation driven by native judgement; no asset loading or audio.</summary>
public sealed class TaikoLumenPresentation
{
    private readonly PlayableChart _chart;
    private readonly ITaikoJudgementSource _judgement;
    private readonly LumenSceneLayer[] _background;
    private readonly LumenSceneLayer[] _foreground;
    private readonly int _flightsAt; // foreground index the hit flights are drawn at
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> _notes;
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? _handNotes;
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? _synchroNotes;
    private readonly LumenSceneLayer? _rareNote;
    private readonly LumenSceneLayer? _kadonNote;
    private readonly IReadOnlyDictionary<string, LumenSceneLayer>? _noteTexts; // by onp_moji label
    private readonly string[] _noteLabels;
    private readonly LumenSceneLayer _bar;
    private readonly LumenSceneLayer? _branchBar;
    private readonly HashSet<TimeSpan> _checkBars;
    private ChartBranch _shownBranch;
    private readonly LumenPlayer? _branchBubble;
    private readonly LumenSceneLayer _target;
    private readonly LumenPlayer _feedback;
    private readonly LumenPlayer _board;
    private readonly float _hitX;
    private readonly float _hitY;
    private readonly float _hitRadius;
    private int _combo;
    private readonly TaikoGoGoPresentation _gogo;
    private readonly TaikoHitFlights? _flights;
    private readonly TaikoLongNotePresentation? _longNotes;

    public TaikoLumenPresentation(PlayableChart chart, ITaikoJudgementSource judgement,
        IEnumerable<LumenSceneLayer> background, IEnumerable<LumenSceneLayer> foreground,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> notes,
        LumenSceneLayer bar, LumenSceneLayer target, LumenPlayer feedback, LumenPlayer board,
        TaikoHitFlights? flights = null, TaikoLongNotePresentation? longNotes = null, int? flightsAt = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? handNotes = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? synchroNotes = null,
        LumenSceneLayer? rareNote = null,
        LumenSceneLayer? kadonNote = null,
        IReadOnlyDictionary<string, LumenSceneLayer>? noteTexts = null,
        LumenSceneLayer? branchBar = null,
        LumenPlayer? branchBubble = null)
    {
        _branchBar = branchBar;
        _branchBubble = branchBubble;
        _checkBars = [.. chart.Branching?.Checks.Select(static check => check.Start) ?? []];
        _noteTexts = noteTexts;
        _noteLabels = TaikoNoteText.Labels(chart);
        _rareNote = rareNote;
        _kadonNote = kadonNote;
        _handNotes = handNotes;
        _synchroNotes = synchroNotes;
        _chart = chart;
        _judgement = judgement;
        _background = background.ToArray();
        _foreground = foreground.ToArray();
        _flightsAt = Math.Clamp(flightsAt ?? _foreground.Length, 0, _foreground.Length);
        _notes = notes;
        _bar = bar;
        _target = target;
        _feedback = feedback;
        _board = board;
        _flights = flights;
        _longNotes = longNotes;
        _gogo = new TaikoGoGoPresentation(now => chart.IsGoGoAt(now, judgement.BranchAt(now)), active =>
        {
            callback(_target.Player, "SetGogo", LumenHostValue.FromBoolean(active));
            GoGoChanged?.Invoke(active);
        });
        // Lanes built again on the same movie (practice retries, replay jumps): the target back in its place first.
        foreach (var clip in TargetClips)
            target.Player.SetInstanceOffset(clip, 0, 0);
        if (!target.Player.TryGetInstanceBounds("target", out var bounds))
            throw new InvalidDataException("Gameplay lane is missing rendered target geometry.");
        (_hitX, _hitY) = target.Transform.Transform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _hitRadius = _hitY - target.Transform.Transform(bounds.X + bounds.Width / 2, bounds.Y).Y;
        judgement.Judged += onJudged;
        callback(_board, "SetComboCount", LumenHostValue.FromNumber(0));
        // Traced: SetBranchAvailable(false) for every chart without 譜面分岐; true shows the normal route's lane.
        callback(_target.Player, "SetBranchAvailable", LumenHostValue.FromBoolean(chart.Branching is not null));
    }

    /// <summary>Shows a combo count at once (a reviewed play's restored state).</summary>
    public void SetCombo(int combo)
    {
        if (_combo == combo)
            return;
        _combo = combo;
        callback(_board, "SetComboCount", LumenHostValue.FromNumber(combo));
    }

    public void Hit(TaikoInputAction action)
    {
        var don = action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon;
        var left = action is TaikoInputAction.LeftDon or TaikoInputAction.LeftKa;
        if (!_target.Player.TryGotoLabel("effect", don ? "don_s" : "katsu_s"))
            throw new InvalidDataException("Gameplay lane has no drum feedback state.");
        callback(_board, "SetTaikoHit", LumenHostValue.FromString(
            (left ? "left_" : "right_") + (don ? "don" : "katsu")));
    }

    public event Action<bool>? GoGoChanged;

    /// <summary>The ドロン option: notes are not drawn, only the text under them.</summary>
    public bool Doron { get; set; }

    /// <summary>The centre of the lane's hit target on the 1280x720 stage.</summary>
    /// <remarks>Includes the target's TJA #JPOSSCROLL move as of the last <see cref="Update"/>.</remarks>
    public (float X, float Y) HitPoint => (_hitX + _moved.X, _hitY + _moved.Y);

    // The target's #JPOSSCROLL offset last applied to its clips.
    private (float X, float Y) _moved;

    // Moves lane_hit's target, hit ring and Go-Go fire (not the lane or its flash) and the judgement text with the
    // chart's #JPOSSCROLL, as OpenTaiko moves its judge frame.
    private void moveTarget(TimeSpan time)
    {
        if (_chart.JudgeMoves.IsEmpty) return;
        var (x, y) = _chart.JudgeOffsetAt(time);
        // Every tick, not only on a change: a clip the timeline places again (the fire at each Go-Go) comes back unmoved.
        _moved = ((float)x, (float)y);
        offsetTarget(_moved.X, _moved.Y);
    }

    private void offsetTarget(float x, float y)
    {
        // ponytail: stage pixels to the movie's by the layer's scale alone; the clips' own parents assumed unscaled.
        var (sx, sy) = (x / _target.Transform.M11, y / _target.Transform.M22);
        foreach (var clip in TargetClips)
            _target.Player.SetInstanceOffset(clip, sx, sy);
    }

    // lane_hit's children at the target: its ring, the hit ring and the Go-Go fire, which shares the gogo clip with
    // the lane's Go-Go glow (a shape, character 19) and stays with the lane, as do the branch colour and flash.
    private static readonly string[] TargetClips = ["target", "hit_effect", "gogo/#37"];

    /// <summary>Half the hit target's height on the stage.</summary>
    public float HitRadius => _hitRadius;

    public void Update(TimeSpan time)
    {
        _longNotes?.Update(time);
        _gogo.Update(time);
        moveTarget(time);
        // The lane takes the route a check picked at once (lane_hit plays its normal_kurouto etc. transition),
        // and Don-chan's bubble says which way it went (traced: same tick; the movie stops itself after).
        if (_judgement.Branch != _shownBranch)
        {
            _branchBubble?.GotoLabel(_judgement.Branch > _shownBranch ? "level_up" : "level_down", play: true);
            _shownBranch = _judgement.Branch;
            callback(_target.Player, "SetBranch", LumenHostValue.FromNumber((int)_shownBranch));
        }
    }

    // characterSlot: background index the character is drawn before (it sits on its backdrop,
    // under the runners, lane, notes and overlays).
    public LumenRenderSnapshot CreateSnapshot(TimeSpan time, float interpolation,
        Func<LumenPlayer, float>? playerInterpolation = null, LumenSceneLayer? character = null,
        int characterSlot = 0)
    {
        var (back, front) = CreateLayers(time, character, characterSlot);
        return new LumenScenePlayer(1280, 720, [.. back, .. front]).CreateRenderSnapshot(interpolation, playerInterpolation);
    }

    /// <summary>
    /// The lane's layers in draw order, split at the notes: backgrounds, lane, bar lines and notes;
    /// then the overlays in front (two lanes are merged back, back, front, front).
    /// </summary>
    // Notes taken off the lane without a judgement (the audio calibration's hit notes).
    private readonly HashSet<int> _removed = [];

    /// <summary>Stops drawing note <paramref name="index"/>, with no judgement shown.</summary>
    public void RemoveNote(int index) => _removed.Add(index);

    /// <summary>The target's hit ring (a GOOD's), alone: no judgement, combo or score.</summary>
    public void FlashTarget() => _target.Player.TryGotoLabel("hit_effect", "hit_ryo");

    public (List<LumenSceneLayer> Back, List<LumenSceneLayer> Front) CreateLayers(TimeSpan time,
        LumenSceneLayer? character = null, int characterSlot = 0)
    {
        var layers = new List<LumenSceneLayer>(_background);
        if (character is { } don)
            layers.Insert(Math.Clamp(characterSlot, 0, layers.Count), don);
        // Each route's bar lines show while that route is played; without bars of their own, one list for all.
        _barStreams ??= _chart.RouteControls.Any(static route => !route.Bars.IsEmpty)
            ? [.. Enum.GetValues<ChartBranch>().Select(route => ((ChartBranch?)route, barTimes(_chart.BarLinesOn(route))))]
            : [(null, barTimes(_chart.BarLines))];
        foreach (var (route, bars) in _barStreams)
            foreach (var (bar, from, until) in bars)
            {
                if (!bar.IsVisible || time < from || time > until) continue;
                var at = _judgement.BranchAt(bar.Time);
                if (route is null || route == at)
                    addAt(layers, _branchBar is { } yellow && _checkBars.Contains(bar.Time) ? yellow : _bar, bar.Time, time, at, rotate: true);
            }
        // Notes of every kind in chart order, later ones behind (the game gives each spawned note a
        // deeper depth), so an earlier roll body covers the notes that follow it.
        var notes = new List<(TimeSpan Start, LumenSceneLayer Layer)>();
        if (_longNotes is not null)
            notes.AddRange(_longNotes.NoteLayers(time,
                (noteTime, controlTime, route) => point(noteTime, time, controlTime, route), origin(time), bodies: !Doron));
        var hitNotes = new List<LumenSceneLayer>(1);
        var texts = new List<LumenSceneLayer>();
        var noteWindows = _noteWindows ??= [.. _chart.HitObjects.Select(note => window(note.StartTime, note.Branch))];
        for (var index = _chart.NoteCount - 1; index >= 0; index--)
        {
            if (time < noteWindows[index].From || time > noteWindows[index].Until) continue;
            // A miss is judged at the timing window, but its note keeps scrolling out of view.
            if (_removed.Contains(index) || _judgement.IsJudged(index) && !_judgement.IsMissed(index)) continue;
            hitNotes.Clear();
            var note = _chart.HitObjects[index];
            if (!PlayableChart.IsOn(note.Branch, _judgement.BranchAt(note.StartTime))) continue;
            // Hand notes get their own movie when the scene has one (two players), else the big note's.
            var template = note.IsKadon && _kadonNote is { } kadon ? kadon
                : note.IsRare && _rareNote is { } rare ? rare
                : note.IsSynchro && _synchroNotes?.GetValueOrDefault(note.Kind) is { } synchro ? synchro
                : note.IsHand && _handNotes?.GetValueOrDefault(note.Kind) is { } hand ? hand
                : _notes[note.Kind];
            addAt(hitNotes, template, note.StartTime, time, note.Branch, sudden: true);
            if (hitNotes.Count != 0 && !Doron) notes.Add((_chart.HitObjects[index].StartTime, hitNotes[0]));
            // Traced: each note spawns an onp_moji clone (ドン/カッ label) 82 px below it that scrolls with it.
            if (_noteTexts?.GetValueOrDefault(_noteLabels[index]) is { } text)
                addAt(texts, text, note.StartTime, time, note.Branch, TaikoNoteText.Offset, sudden: true);
        }
        // Balloons and kusudamas carry their text until their overlay takes over (a balloon waiting on the
        // target keeps it there).
        for (var index = 0; index < _chart.LongNotes.Length; index++)
        {
            var note = _chart.LongNotes[index];
            var held = _longNotes?.IsHeld(index, time) == true;
            if (!PlayableChart.IsOn(note.Branch, _judgement.BranchAt(note.StartTime))) continue;
            if (note.IsBalloon && (time < note.StartTime || held)
                && _noteTexts?.GetValueOrDefault(TaikoNoteText.Balloon(note.Kind)) is { } text)
                addAt(texts, text, held ? time : note.StartTime, time, note.Branch, TaikoNoteText.Offset);
        }
        layers.AddRange(notes.OrderByDescending(note => note.Start).Select(note => note.Layer));
        layers.AddRange(texts);
        // Foreground in depth order (roll counter and balloon/kusudama overlays included); the hit
        // flights sit at their template's depth.
        var atTarget = _moved == default ? null : (_longNotes?.TargetOverlays ?? []).Append(_feedback).ToHashSet();
        var foreground = atTarget is null ? _foreground : [.. _foreground.Select(layer => atTarget.Contains(layer.Player)
            ? layer with { Transform = layer.Transform with { X = layer.Transform.X + _moved.X, Y = layer.Transform.Y + _moved.Y } }
            : layer)];
        var front = new List<LumenSceneLayer>(foreground.Take(_flightsAt));
        // The flights move with the target (OpenTaiko keeps them where they'd be relative to it, landing included).
        if (_flights is not null)
            front.AddRange(_moved == default ? _flights.ActiveLayers : _flights.ActiveLayers.Select(layer => layer with
                { Transform = layer.Transform with { X = layer.Transform.X + _moved.X, Y = layer.Transform.Y + _moved.Y } }));
        front.AddRange(foreground.Skip(_flightsAt));
        return (layers, front);
    }

    // rotate: a bar line stands across the way it travels (TJA complex scroll); notes stay upright (OpenTaiko).
    // sudden: a note (or its text) that #SUDDEN hides until shortly before its time and holds still until then.
    private void addAt(List<LumenSceneLayer> layers, LumenSceneLayer template,
        TimeSpan noteTime, TimeSpan time, ChartBranch? route, float yOffset = 0, bool rotate = false, bool sudden = false)
    {
        if (sudden && _chart.ScrollPointAt(noteTime, route).Show is { } show && noteTime - time > show)
            return;
        var (x, y) = point(noteTime, time, noteTime, route, sudden);
        y += yOffset;
        if (x < -100 || x > 1380 || y < -100 || y > 820)
            return;
        var transform = LumenMatrix.Identity with { X = x, Y = y };
        if (rotate && _chart.ScrollPointAt(noteTime, route) is { Imaginary: not 0 } scroll)
        {
            var angle = Math.Atan2(scroll.Imaginary, scroll.Multiplier);
            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            transform = transform with { M11 = cos, M12 = sin, M21 = -sin, M22 = cos };
        }
        layers.Add(template with { Transform = transform });
    }

    // The target's centre at time (moved by #JPOSSCROLL).
    private (float X, float Y) origin(TimeSpan time)
    {
        if (_chart.JudgeMoves.IsEmpty) return (_hitX, _hitY);
        var (x, y) = _chart.JudgeOffsetAt(time);
        return (_hitX + (float)x, _hitY + (float)y);
    }

    // Where a note of noteTime is at time: its own scroll's way (TJA complex scroll: x+yi), from the target.
    // sudden: a hit note waits where #SUDDEN's wait time puts it (OpenTaiko: hit notes only, not rolls or bar lines).
    private (float X, float Y) point(TimeSpan noteTime, TimeSpan time, TimeSpan controlTime, ChartBranch? route, bool sudden = false)
    {
        var (ox, oy) = origin(time);
        var scroll = _chart.ScrollPointAt(controlTime, route);
        var now = sudden && scroll.Wait is { } wait && noteTime - time > wait ? noteTime - wait : time;
        // #HBSCROLL/#BMSCROLL: by the beats left (four to 948 px), else by the seconds at the note's tempo.
        // ponytail: the beat count is the main route's; a 譜面分岐 route with its own tempo scrolls by it too.
        var distance = scroll.ByBeat ? (_chart.BeatAt(noteTime) - _chart.BeatAt(now)) * 948 / 4
            : (noteTime - now).TotalSeconds * ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(controlTime, route), 1);
        return ((float)Math.Clamp(ox + distance * scroll.Multiplier, -100_000, 100_000),
            (float)Math.Clamp(oy + distance * scroll.Imaginary, -100_000, 100_000));
    }

    private double pixelsPerSecond(TimeSpan controlTime, ChartBranch? route) =>
        ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(controlTime, route), _chart.ScrollAt(controlTime, route));

    /// <summary>
    /// Scroll velocity on the 1280x720 stage: four beats at scroll 1 span 948 pixels,
    /// including the offscreen approach. The hit target's position does not set the speed.
    /// </summary>
    // Public reference and independent runtime measurements: docs/development/gameplay-integration.md.
    public static double ScrollPixelsPerSecond(double beatsPerMinute, double scrollMultiplier) =>
        beatsPerMinute / 240 * 948 * scrollMultiplier;

    // When each note and bar is on the lane, so a frame skips the rest of a chart with one comparison each.
    // ponytail: still a pass over every note, a cursor needs care as scroll changes reorder arrivals.
    private (TimeSpan From, TimeSpan Until)[]? _noteWindows;
    private (ChartBranch? Route, (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] Bars)[]? _barStreams;
    private (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] barTimes(ImmutableArray<ChartBarLine> bars) =>
        [.. bars.Select(bar => barWindow(bar.Time) is var window ? (bar, window.From, window.Until) : default)];

    private (TimeSpan From, TimeSpan Until) window(TimeSpan noteTime, ChartBranch? route)
    {
        // Gimmicks (backwards or slanted scroll, a moving target): the note may come from any side, so it is
        // listed for as long as it is within a screen's diagonal of the target.
        var scroll = _chart.ScrollPointAt(noteTime, route);
        if (scroll.ByBeat && !_chart.Beats.IsEmpty)
        {
            // Within a screen's diagonal of the target, in beats; a #DELAY on the way keeps it listed longer.
            var beat = _chart.BeatAt(noteTime);
            var reachBeats = 1500 / (948 / 4.0 * Math.Max(0.01, Math.Sqrt(scroll.Multiplier * scroll.Multiplier + scroll.Imaginary * scroll.Imaginary)));
            var from = _chart.TimeAtBeat(beat - reachBeats);
            if (scroll.Show is { } appear && noteTime - appear > from)
                from = noteTime - appear;
            return (from, _chart.TimeAtBeat(beat + reachBeats) + TimeSpan.FromSeconds(1));
        }
        // #SUDDEN: shown from its appear time (a note held still may wait on screen from the start).
        if (scroll.Show is not null || scroll.Wait is not null)
            return (scroll.Show is { } show ? noteTime - show : TimeSpan.MinValue, noteTime + TimeSpan.FromSeconds(1500 / Math.Max(1,
                ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(noteTime, route), Math.Sqrt(scroll.Multiplier * scroll.Multiplier + scroll.Imaginary * scroll.Imaginary)))));
        if (scroll.Multiplier > 0 && scroll.Imaginary == 0 && _chart.JudgeMoves.IsEmpty)
            return VisibleWindow(noteTime, pixelsPerSecond(noteTime, route), _hitX);
        var speed = ScrollPixelsPerSecond(_chart.BeatsPerMinuteAt(noteTime, route), Math.Sqrt(scroll.Multiplier * scroll.Multiplier + scroll.Imaginary * scroll.Imaginary));
        var reach = TimeSpan.FromSeconds(1500 / speed);
        return speed < 1 ? (TimeSpan.MinValue, TimeSpan.MaxValue)
            : (noteTime - reach, noteTime + reach);
    }

    // A bar line follows whichever route is playing, so it stays listed for as long as the slowest would show it.
    private (TimeSpan From, TimeSpan Until) barWindow(TimeSpan time) => _chart.ScrollPointAt(time).ByBeat ? window(time, null) :
        VisibleWindow(time, Enum.GetValues<ChartBranch>().Min(route => pixelsPerSecond(time, route)), _hitX);

    /// <summary>When something moving at this speed is between x -100 and 1380, the lane's drawn range; always, if it barely moves.</summary>
    public static (TimeSpan From, TimeSpan Until) VisibleWindow(TimeSpan noteTime, double pixelsPerSecond, float hitX) =>
        pixelsPerSecond < 1
            ? (TimeSpan.MinValue, TimeSpan.MaxValue)
            : (noteTime - TimeSpan.FromSeconds((1380 - hitX) / pixelsPerSecond), noteTime + TimeSpan.FromSeconds((hitX + 100) / pixelsPerSecond));

    /// <summary>The combo shown is set from outside (<see cref="SetCombo"/>), not counted from the judgements shown (an online partner's lane).</summary>
    public bool ExternalCombo { get; set; }

    private void onJudged(TaikoNoteJudgement result)
    {
        _flights?.Trigger(result);
        if (result.StrongHitCompleted || result.Placeholder)
            return;
        if (!ExternalCombo)
        {
            _combo = result.Result == TaikoHitResult.Miss ? 0 : _combo + 1;
            callback(_board, "SetComboCount", LumenHostValue.FromNumber(_combo));
        }
        if (result.TimedOut)
            return;
        var label = result.Result switch
        {
            TaikoHitResult.Great => "ryo",
            TaikoHitResult.Good => "ka",
            _ => "huka",
        };
        if (!_target.Player.TryGotoLabel("hit_effect", (result.HitObject.IsStrong ? "hit_dai_" : "hit_") + label)
            || !_feedback.TryGotoLabel("", "hit_" + label
                + (result.HitObject.IsStrong && result.Result != TaikoHitResult.Miss ? "_big" : "")))
            throw new InvalidDataException("Gameplay movie is missing a judgement state.");
        // Traced (session6/12/27): a press that judges a note turns the lane's don_s/katsu_s into the brighter
        // don_hit/katsu_hit in the same tick. ponytail: bads keep the plain flash (none traced); don_b/katsu_b unseen.
        if (result.Result is TaikoHitResult.Great or TaikoHitResult.Good)
            lightHit(result);
    }

    private string? _hitLight;

    private void lightHit(TaikoNoteJudgement result)
    {
        _hitLight = result.HitObject.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa ? "katsu_hit" : "don_hit";
        _target.Player.TryGotoLabel("effect", _hitLight);
    }

    /// <summary>
    /// A big note's other hand (absorbed, no judgement): its press flash gives way to the hit light again, so a
    /// two-hand hit stays lit as a hit.
    /// </summary>
    public void KeepHitLight()
    {
        if (_hitLight is { } label)
            _target.Player.TryGotoLabel("effect", label);
    }

    private static void callback(LumenPlayer player, string name, LumenHostValue value)
    {
        if (!player.TryInvokeCallback(name, [value]))
            throw new InvalidDataException($"Gameplay movie is missing callback '{name}'.");
    }
}
