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
    private readonly LumenSceneLayer? _rareNote;
    private readonly IReadOnlyDictionary<string, LumenSceneLayer>? _noteTexts; // by onp_moji label
    private readonly string[] _noteLabels;
    private readonly LumenSceneLayer _bar;
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

    public TaikoLumenPresentation(PlayableChart chart, TaikoJudgementSession judgement,
        IEnumerable<LumenSceneLayer> background, IEnumerable<LumenSceneLayer> foreground,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> notes,
        LumenSceneLayer bar, LumenSceneLayer target, LumenPlayer feedback, LumenPlayer board,
        TaikoHitFlights? flights = null, TaikoLongNotePresentation? longNotes = null, int? flightsAt = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? handNotes = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? synchroNotes = null,
        LumenSceneLayer? rareNote = null,
        IReadOnlyDictionary<string, LumenSceneLayer>? noteTexts = null)
    {
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
        _gogo = new TaikoGoGoPresentation(chart.EffectPoints, active =>
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

    /// <summary>The centre of the lane's hit target on the 1280x720 stage.</summary>
    public (float X, float Y) HitPoint => (_hitX, _hitY);

    /// <summary>Half the hit target's height on the stage.</summary>
    public float HitRadius => _hitRadius;

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
        foreach (var bar in _chart.BarLines)
            if (bar.IsVisible)
                addAt(layers, _bar, bar.Time, time);
        // Notes of every kind in chart order, later ones behind (the game gives each spawned note a
        // deeper depth), so an earlier roll body covers the notes that follow it.
        var notes = new List<(TimeSpan Start, LumenSceneLayer Layer)>();
        if (_longNotes is not null)
            notes.AddRange(_longNotes.NoteLayers(time,
                (noteTime, controlTime) => position(noteTime, time, controlTime), _hitX, _hitY));
        var hitNotes = new List<LumenSceneLayer>(1);
        var texts = new List<LumenSceneLayer>();
        for (var index = _chart.NoteCount - 1; index >= 0; index--)
        {
            // A miss is judged at the timing window, but its note keeps scrolling out of view.
            if (_removed.Contains(index) || _judgement.IsJudged(index) && !_judgement.IsMissed(index)) continue;
            hitNotes.Clear();
            var note = _chart.HitObjects[index];
            // Hand notes get their own movie when the scene has one (two players), else the big note's.
            var template = note.IsRare && _rareNote is { } rare ? rare
                : note.IsSynchro && _synchroNotes?.GetValueOrDefault(note.Kind) is { } synchro ? synchro
                : note.IsHand && _handNotes?.GetValueOrDefault(note.Kind) is { } hand ? hand
                : _notes[note.Kind];
            addAt(hitNotes, template, note.StartTime, time);
            if (hitNotes.Count != 0) notes.Add((_chart.HitObjects[index].StartTime, hitNotes[0]));
            // Traced: each note spawns an onp_moji clone (ドン/カッ label) 82 px below it that scrolls with it.
            if (_noteTexts?.GetValueOrDefault(_noteLabels[index]) is { } text)
                addAt(texts, text, note.StartTime, time, yOffset: TaikoNoteText.Offset);
        }
        // Balloons and kusudamas carry their text until their overlay takes over (a balloon waiting on the
        // target keeps it there).
        for (var index = 0; index < _chart.LongNotes.Length; index++)
        {
            var note = _chart.LongNotes[index];
            var held = _longNotes?.IsHeld(index, time) == true;
            if (note.IsBalloon && (time < note.StartTime || held)
                && _noteTexts?.GetValueOrDefault(TaikoNoteText.Balloon(note.Kind)) is { } text)
                addAt(texts, text, held ? time : note.StartTime, time, yOffset: TaikoNoteText.Offset);
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
        TimeSpan noteTime, TimeSpan time, float yOffset = 0)
    {
        var x = position(noteTime, time, noteTime);
        if (x < -100 || x > 1380)
            return;
        layers.Add(template with { Transform = LumenMatrix.Identity with { X = x, Y = _hitY + yOffset } });
    }

    private float position(TimeSpan noteTime, TimeSpan time, TimeSpan controlTime)
    {
        var bpm = _chart.TimingPoints[0].BeatsPerMinute;
        foreach (var point in _chart.TimingPoints)
        {
            if (point.Time > controlTime)
                break;
            bpm = point.BeatsPerMinute;
        }
        var multiplier = 1d;
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
