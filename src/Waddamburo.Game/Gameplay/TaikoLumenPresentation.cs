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
    public (float X, float Y) HitPoint => (_hitX, _hitY);

    /// <summary>Half the hit target's height on the stage.</summary>
    public float HitRadius => _hitRadius;

    public void Update(TimeSpan time)
    {
        _longNotes?.Update(time);
        _gogo.Update(time);
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
                    addAt(layers, _branchBar is { } yellow && _checkBars.Contains(bar.Time) ? yellow : _bar, bar.Time, time, at);
            }
        // Notes of every kind in chart order, later ones behind (the game gives each spawned note a
        // deeper depth), so an earlier roll body covers the notes that follow it.
        var notes = new List<(TimeSpan Start, LumenSceneLayer Layer)>();
        if (_longNotes is not null)
            notes.AddRange(_longNotes.NoteLayers(time,
                (noteTime, controlTime, route) => position(noteTime, time, controlTime, route), _hitX, _hitY, bodies: !Doron));
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
            var template = note.IsRare && _rareNote is { } rare ? rare
                : note.IsSynchro && _synchroNotes?.GetValueOrDefault(note.Kind) is { } synchro ? synchro
                : note.IsHand && _handNotes?.GetValueOrDefault(note.Kind) is { } hand ? hand
                : _notes[note.Kind];
            addAt(hitNotes, template, note.StartTime, time, note.Branch);
            if (hitNotes.Count != 0 && !Doron) notes.Add((_chart.HitObjects[index].StartTime, hitNotes[0]));
            // Traced: each note spawns an onp_moji clone (ドン/カッ label) 82 px below it that scrolls with it.
            if (_noteTexts?.GetValueOrDefault(_noteLabels[index]) is { } text)
                addAt(texts, text, note.StartTime, time, note.Branch, TaikoNoteText.Offset);
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
        var front = new List<LumenSceneLayer>(_foreground.Take(_flightsAt));
        if (_flights is not null)
            front.AddRange(_flights.ActiveLayers);
        front.AddRange(_foreground.Skip(_flightsAt));
        return (layers, front);
    }

    private void addAt(List<LumenSceneLayer> layers, LumenSceneLayer template,
        TimeSpan noteTime, TimeSpan time, ChartBranch? route, float yOffset = 0)
    {
        var x = position(noteTime, time, noteTime, route);
        if (x < -100 || x > 1380)
            return;
        layers.Add(template with { Transform = LumenMatrix.Identity with { X = x, Y = _hitY + yOffset } });
    }

    private float position(TimeSpan noteTime, TimeSpan time, TimeSpan controlTime, ChartBranch? route) =>
        (float)Math.Clamp(_hitX + (noteTime - time).TotalSeconds * pixelsPerSecond(controlTime, route), -100_000, 100_000);

    // Product layout: four beats span the visible lane at scroll 1.
    private double pixelsPerSecond(TimeSpan controlTime, ChartBranch? route) =>
        _chart.BeatsPerMinuteAt(controlTime, route) / 60 * (1280 - _hitX) / 4 * _chart.ScrollAt(controlTime, route);

    // When each note and bar is on the lane, so a frame skips the rest of a chart with one comparison each.
    // ponytail: still a pass over every note, a cursor needs care as scroll changes reorder arrivals.
    private (TimeSpan From, TimeSpan Until)[]? _noteWindows;
    private (ChartBranch? Route, (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] Bars)[]? _barStreams;
    private (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] barTimes(ImmutableArray<ChartBarLine> bars) =>
        [.. bars.Select(bar => barWindow(bar.Time) is var window ? (bar, window.From, window.Until) : default)];

    private (TimeSpan From, TimeSpan Until) window(TimeSpan noteTime, ChartBranch? route) =>
        VisibleWindow(noteTime, pixelsPerSecond(noteTime, route), _hitX);

    // A bar line follows whichever route is playing, so it stays listed for as long as the slowest would show it.
    private (TimeSpan From, TimeSpan Until) barWindow(TimeSpan time) =>
        VisibleWindow(time, Enum.GetValues<ChartBranch>().Min(route => pixelsPerSecond(time, route)), _hitX);

    /// <summary>When something moving at this speed is between x -100 and 1380, the lane's drawn range; always, if it barely moves.</summary>
    public static (TimeSpan From, TimeSpan Until) VisibleWindow(TimeSpan noteTime, double pixelsPerSecond, float hitX) =>
        pixelsPerSecond < 1
            ? (TimeSpan.MinValue, TimeSpan.MaxValue)
            : (noteTime - TimeSpan.FromSeconds((1380 - hitX) / pixelsPerSecond), noteTime + TimeSpan.FromSeconds((hitX + 100) / pixelsPerSecond));

    private void onJudged(TaikoNoteJudgement result)
    {
        _flights?.Trigger(result);
        if (result.StrongHitCompleted)
            return;
        _combo = result.Result == TaikoHitResult.Miss ? 0 : _combo + 1;
        callback(_board, "SetComboCount", LumenHostValue.FromNumber(_combo));
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
    }

    private static void callback(LumenPlayer player, string name, LumenHostValue value)
    {
        if (!player.TryInvokeCallback(name, [value]))
            throw new InvalidDataException($"Gameplay movie is missing callback '{name}'.");
    }
}
