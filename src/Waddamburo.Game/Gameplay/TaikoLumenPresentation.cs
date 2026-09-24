using Waddamburo.Catalog;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Authored presentation driven by native judgement; no asset loading or audio.</summary>
public sealed class TaikoLumenPresentation
{
    private readonly PlayableChart _chart;
    private readonly TaikoJudgementSession _judgement;
    private readonly LumenSceneLayer[] _background;
    private readonly LumenSceneLayer[] _foreground;
    private readonly int _flightsAt; // foreground index the hit flights are drawn at
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> _notes;
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? _handNotes;
    private readonly IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? _synchroNotes;
    private readonly LumenSceneLayer _bar;
    private readonly LumenSceneLayer _target;
    private readonly LumenPlayer _feedback;
    private readonly LumenPlayer _board;
    private readonly float _hitX;
    private readonly float _hitY;
    private int _combo;
    private readonly TaikoGoGoPresentation _gogo;
    private readonly TaikoHitFlights? _flights;
    private readonly TaikoLongNotePresentation? _longNotes;

    public TaikoLumenPresentation(PlayableChart chart, TaikoJudgementSession judgement,
        IEnumerable<LumenSceneLayer> background, IEnumerable<LumenSceneLayer> foreground,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> notes,
        LumenSceneLayer bar, LumenSceneLayer target, LumenPlayer feedback, LumenPlayer board,
        TaikoHitFlights? flights = null, TaikoLongNotePresentation? longNotes = null, int? flightsAt = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? handNotes = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? synchroNotes = null)
    {
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
        _gogo = new TaikoGoGoPresentation(chart.EffectPoints, active =>
        {
            callback(_target.Player, "SetGogo", LumenHostValue.FromBoolean(active));
            GoGoChanged?.Invoke(active);
        });
        if (!target.Player.TryGetInstanceBounds("target", out var bounds))
            throw new InvalidDataException("Gameplay lane is missing rendered target geometry.");
        (_hitX, _hitY) = target.Transform.Transform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        judgement.Judged += onJudged;
        callback(_board, "SetComboCount", LumenHostValue.FromNumber(0));
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

    public void Update(TimeSpan time)
    {
        _longNotes?.Update(time);
        _gogo.Update(time);
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
    public (List<LumenSceneLayer> Back, List<LumenSceneLayer> Front) CreateLayers(TimeSpan time,
        LumenSceneLayer? character = null, int characterSlot = 0)
    {
        var layers = new List<LumenSceneLayer>(_background);
        if (character is { } don)
            layers.Insert(Math.Clamp(characterSlot, 0, layers.Count), don);
        foreach (var bar in _chart.BarLines)
            if (bar.IsVisible)
                addAt(layers, _bar, bar.Time, time, scroll: false);
        // Notes of every kind in chart order, later ones behind (the game gives each spawned note a
        // deeper depth), so an earlier roll body covers the notes that follow it.
        var notes = new List<(TimeSpan Start, LumenSceneLayer Layer)>();
        if (_longNotes is not null)
            notes.AddRange(_longNotes.NoteLayers(time,
                (noteTime, controlTime) => position(noteTime, time, controlTime, scroll: true), _hitX, _hitY));
        var hitNotes = new List<LumenSceneLayer>(1);
        for (var index = _chart.NoteCount - 1; index >= 0; index--)
        {
            if (_judgement.IsJudged(index)) continue;
            hitNotes.Clear();
            var note = _chart.HitObjects[index];
            // Hand notes get their own movie when the scene has one (two players), else the big note's.
            var template = note.IsSynchro && _synchroNotes?.GetValueOrDefault(note.Kind) is { } synchro ? synchro
                : note.IsHand && _handNotes?.GetValueOrDefault(note.Kind) is { } hand ? hand
                : _notes[note.Kind];
            addAt(hitNotes, template, note.StartTime, time, scroll: true);
            if (hitNotes.Count != 0) notes.Add((_chart.HitObjects[index].StartTime, hitNotes[0]));
        }
        layers.AddRange(notes.OrderByDescending(note => note.Start).Select(note => note.Layer));
        // Foreground in depth order (roll counter and balloon/kusudama overlays included); the hit
        // flights sit at their template's depth.
        var front = new List<LumenSceneLayer>(_foreground.Take(_flightsAt));
        if (_flights is not null)
            front.AddRange(_flights.ActiveLayers);
        front.AddRange(_foreground.Skip(_flightsAt));
        return (layers, front);
    }

    private void addAt(List<LumenSceneLayer> layers, LumenSceneLayer template,
        TimeSpan noteTime, TimeSpan time, bool scroll)
    {
        var x = position(noteTime, time, noteTime, scroll);
        if (x < _hitX - 100 || x > 1380)
            return;
        layers.Add(template with { Transform = LumenMatrix.Identity with { X = x, Y = _hitY } });
    }

    private float position(TimeSpan noteTime, TimeSpan time, TimeSpan controlTime, bool scroll)
    {
        var bpm = _chart.TimingPoints[0].BeatsPerMinute;
        foreach (var point in _chart.TimingPoints)
        {
            if (point.Time > controlTime)
                break;
            bpm = point.BeatsPerMinute;
        }
        var multiplier = 1d;
        if (scroll)
            foreach (var point in _chart.ScrollPoints)
            {
                if (point.Time > controlTime)
                    break;
                multiplier = point.Multiplier;
            }
        // Product layout: four beats span the visible lane at scroll 1.
        return (float)Math.Clamp(_hitX + (noteTime - time).TotalSeconds * bpm / 60
            * (1280 - _hitX) / 4 * multiplier, -100_000, 100_000);
    }

    private void onJudged(TaikoNoteJudgement result)
    {
        _flights?.Trigger(result);
        if (result.StrongHitCompleted)
            return;
        _combo = result.Result == TaikoHitResult.Miss ? 0 : _combo + 1;
        callback(_board, "SetComboCount", LumenHostValue.FromNumber(_combo));
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
