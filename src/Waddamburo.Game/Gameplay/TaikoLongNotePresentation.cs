using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Owns isolated authored roll movies and the active long-note counters.</summary>
public sealed class TaikoLongNotePresentation
{
    private readonly PlayableChart _chart;
    private readonly TaikoJudgementSession _session;
    private readonly Func<PlayableLongNoteKind, LumenSceneLayer> _createNote;
    private readonly Dictionary<int, LumenSceneLayer> _visible = [];
    private readonly LumenSceneLayer _counter;
    private readonly LumenSceneLayer _balloon;
    private readonly IDonPresentationController? _don;
    private readonly TaikoHitFlights? _flights;
    private int? _active;
    private bool _counterVisible;
    private bool _balloonVisible;

    public TaikoLongNotePresentation(PlayableChart chart, TaikoJudgementSession session,
        Func<PlayableLongNoteKind, LumenSceneLayer> createNote, LumenSceneLayer counter,
        LumenSceneLayer balloon, TaikoHitFlights? flights = null, IDonPresentationController? don = null)
    {
        _chart = chart;
        _session = session;
        _createNote = createNote;
        _counter = counter;
        _balloon = balloon;
        _flights = flights;
        _don = don;
        if (don is not null)
            DonLumenBinding.Attach(balloon.Player, don);
        session.LongNoteHit += hit;
    }

    public void AdvanceAnimations()
    {
        foreach (var layer in _visible.Values)
        {
            layer.Player.Advance();
            if (layer.Player.CallbackNames.Contains("OnUpdate"))
                invoke(layer.Player, "OnUpdate");
        }
        // Counter movies belong to the scene and are advanced exactly once by its player.
        if (_active is null)
        {
            if (!_counter.Player.IsPlaying) _counterVisible = false;
            if (_balloonVisible && !_balloon.Player.IsPlaying)
            {
                _balloonVisible = false;
            }
        }
    }

    public void Update(TimeSpan time)
    {
        if (_active is { } current && (time >= _chart.LongNotes[current].EndTime
            || _session.GetLongNoteProgress(current).IsPopped))
            finish(current);
        if (_active is not null) return;
        for (var index = 0; index < _chart.LongNotes.Length; index++)
        {
            var note = _chart.LongNotes[index];
            if (note.StartTime > time) break;
            if (time < note.EndTime && !_session.GetLongNoteProgress(index).IsPopped)
            {
                begin(index);
                break;
            }
        }
    }

    public IEnumerable<LumenSceneLayer> NoteLayers(TimeSpan time,
        Func<TimeSpan, TimeSpan, float> position, float hitX, float hitY)
    {
        var retained = new HashSet<int>();
        for (var index = _chart.LongNotes.Length - 1; index >= 0; index--)
        {
            var note = _chart.LongNotes[index];
            if (time >= note.EndTime || _session.GetLongNoteProgress(index).IsPopped) continue;
            if (note.IsBalloon && time >= note.StartTime) continue;
            var head = position(note.StartTime, note.StartTime);
            var tail = note.IsBalloon ? head : position(note.EndTime, note.StartTime);
            if (Math.Max(head, tail) < hitX - 100 || Math.Min(head, tail) > 1380) continue;
            retained.Add(index);
            if (!_visible.TryGetValue(index, out var layer))
            {
                layer = _createNote(note.Kind);
                if (!layer.Player.TryGotoLabel("", "level01"))
                    throw new InvalidDataException("Long-note movie has no initial note state.");
                _visible.Add(index, layer);
            }
            if (!note.IsBalloon)
                invoke(layer.Player, "SetWidth", Math.Abs(tail - head));
            yield return layer with
            {
                Transform = LumenMatrix.Identity with { X = head, Y = hitY, M11 = tail < head ? -1 : 1 },
            };
        }
        foreach (var index in _visible.Keys.Where(index => !retained.Contains(index)).ToArray())
            _visible.Remove(index);
    }

    public bool BalloonVisible => _balloonVisible;

    public IEnumerable<LumenSceneLayer> OverlayLayers
    {
        get
        {
            if (_counterVisible) yield return _counter;
            if (_balloonVisible) yield return _balloon;
        }
    }

    public IEnumerable<LumenPlayer> Players => _visible.Values.Select(layer => layer.Player)
        .Append(_counter.Player).Append(_balloon.Player);

    private void begin(int index)
    {
        _active = index;
        var note = _chart.LongNotes[index];
        if (note.IsBalloon)
        {
            _don?.Reset(DonPresentationLayout.Standard);
            invoke(_balloon.Player, "Reset");
            // The opening timeline creates the balloon child two ticks after 'start'.
            // Prepare it before sending a nonzero quota, which also updates that child.
            invoke(_balloon.Player, "SetGekiRendaCount", 0);
            _balloon.Player.Advance();
            _balloon.Player.Advance();
            invoke(_balloon.Player, "SetGekiRendaCount", note.RequiredHits);
            _balloonVisible = true;
            _don?.SetMotion(note.Kind == PlayableLongNoteKind.Kusudama
                ? new DonMotionRequest(0, "don_kusu1P_in", "don_kusu1P_nobeat")
                : new DonMotionRequest(0, null, "don_balloon_nobeat"));
        }
        else
        {
            invoke(_counter.Player, "SetRendaCount", 0);
            _counterVisible = true;
        }
    }

    private void hit(TaikoLongNoteProgress progress)
    {
        if (_active != progress.NoteIndex)
        {
            if (_active is { } previous) finish(previous);
            begin(progress.NoteIndex);
        }
        _flights?.Trigger(progress);
        if (progress.Note.IsBalloon)
        {
            if (progress.IsPopped)
                finish(progress.NoteIndex);
            else
            {
                invoke(_balloon.Player, "SetGekiRendaCount", progress.Note.RequiredHits - progress.Hits);
                // Every hit restarts the pumping motion, which settles back to the no-beat idle.
                var (pump, rest) = motions(progress.Note);
                _don?.SetMotion(new DonMotionRequest(0, pump, rest));
            }
        }
        else
        {
            invoke(_counter.Player, "SetRendaCount", progress.Hits);
            if (_visible.TryGetValue(progress.NoteIndex, out var layer))
                invoke(layer.Player, "HitAction");
        }
    }

    private void finish(int index)
    {
        var progress = _session.GetLongNoteProgress(index);
        if (progress.Note.IsBalloon)
        {
            invoke(_balloon.Player, "GekiRendaEnd", progress.IsPopped ? 0 : 1);
            var kusudama = progress.Note.Kind == PlayableLongNoteKind.Kusudama;
            _don?.SetMotion(new DonMotionRequest(0, (kusudama, progress.IsPopped) switch
            {
                (true, true) => "don_kusu1P_success02",
                (true, false) => "don_kusu1P_failure",
                (false, true) => "don_balloon_success",
                _ => "don_balloon_failure",
            }, null));
        }
        else
            invoke(_counter.Player, "RendaEnd");
        _active = null;
    }

    private static (string Pump, string Idle) motions(PlayableLongNote note) =>
        note.Kind == PlayableLongNoteKind.Kusudama
            ? ("don_kusu1P_loop", "don_kusu1P_nobeat")
            : ("don_balloon_loop", "don_balloon_nobeat");

    private static void invoke(LumenPlayer player, string name, double? argument = null)
    {
        if (!player.TryInvokeCallback(name, argument is { } value ? [LumenHostValue.FromNumber(value)] : []))
            throw new InvalidDataException($"Long-note movie is missing callback '{name}'.");
    }
}
