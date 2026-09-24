using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The kusudama both players hit in two-player gameplay (traced session9-2p): each player's quota is
/// added to the one movie (AddNorma per player) and it counts down the hits both still need.
/// </summary>
public sealed class TaikoSharedKusudama(LumenSceneLayer layer, int players)
{
    public LumenSceneLayer Layer { get; } = layer;

    public int Players { get; } = players;

    private int _lanes; // lanes inside the current ball

    /// <summary>Hits still needed, summed over the players whose kusudama is running.</summary>
    public int Remaining { get; private set; }

    /// <summary>The combined quota was reached: the ball breaks for both players.</summary>
    public bool Popped { get; private set; }

    /// <summary>A lane's kusudama starts. The first lane in opens a fresh ball (an expired ball keeps its count).</summary>
    public void Enter(int requiredHits)
    {
        if (_lanes++ == 0)
            (Popped, Remaining) = (false, 0);
        Remaining += requiredHits;
    }

    /// <summary>One hit from either player; returns the hits still needed.</summary>
    public int Hit()
    {
        Remaining = Math.Max(0, Remaining - 1);
        Popped |= Remaining == 0;
        return Remaining;
    }

    /// <summary>A lane's kusudama ended (popped or expired).</summary>
    public void Leave() => _lanes = Math.Max(0, _lanes - 1);
}

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
    private readonly Action<PlayableLongNoteKind, bool>? _onBalloonCompleted;
    private readonly Action<PlayableLongNoteKind>? _onNoteStarted;
    private int? _active;
    private readonly HashSet<int> _finished = []; // a shared ball can end before the lane's own quota
    private bool _balloonVisible;
    private readonly LumenSceneLayer? _kusudama;
    private readonly TaikoSharedKusudama? _sharedKusudama;
    private LumenSceneLayer _overlay; // the balloon or kusudama overlay in use
    private readonly int _player; // Don slot; the second player's kusudama motions are don_kusu2P_*
    private readonly string _kusu;

    public TaikoLongNotePresentation(PlayableChart chart, TaikoJudgementSession session,
        Func<PlayableLongNoteKind, LumenSceneLayer> createNote, LumenSceneLayer counter,
        LumenSceneLayer balloon, TaikoHitFlights? flights = null, IDonPresentationController? don = null,
        LumenSceneLayer? kusudama = null, Action<PlayableLongNoteKind, bool>? onBalloonCompleted = null,
        Action<PlayableLongNoteKind>? onNoteStarted = null, int player = 0, TaikoSharedKusudama? sharedKusudama = null)
    {
        _player = player;
        _kusu = player == 1 ? "don_kusu2P" : "don_kusu1P";
        _sharedKusudama = sharedKusudama;
        kusudama ??= sharedKusudama?.Layer;
        _chart = chart;
        _session = session;
        _createNote = createNote;
        _counter = counter;
        _balloon = balloon;
        _overlay = balloon;
        _kusudama = kusudama;
        _flights = flights;
        _onBalloonCompleted = onBalloonCompleted;
        _onNoteStarted = onNoteStarted;
        _don = don;
        // The Dons are already playing (another lane may have started first): bind without a reset.
        if (don is not null)
            DonLumenBinding.Attach(balloon.Player, don, reset: false);
        if (kusudama is not null)
        {
            if (don is not null) DonLumenBinding.Attach(kusudama.Player, don, reset: false);
            // Traced once per player, with the player count.
            call(kusudama.Player, "SetPlayerNum", LumenHostValue.FromNumber(sharedKusudama?.Players ?? 1));
            call(kusudama.Player, "SetWaiWai", LumenHostValue.FromBoolean(false));
        }
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
            // The kusudama root stops right after EndResult; its 'kusudama' clip then plays the break
            // (ResultHigh/Low/Miss) and stops once the ball has faded out.
            if (_balloonVisible && !_overlay.Player.IsPlaying
                && !(_overlay == _kusudama && _overlay.Player.IsInstancePlaying("kusudama")))
            {
                _balloonVisible = false;
            }
        }
    }

    public void Update(TimeSpan time)
    {
        if (_active is { } current && (time >= _chart.LongNotes[current].EndTime
            || (shared(current) ? _sharedKusudama!.Popped : _session.GetLongNoteProgress(current).IsPopped)))
            finish(current);
        if (_active is not null) return;
        for (var index = 0; index < _chart.LongNotes.Length; index++)
        {
            var note = _chart.LongNotes[index];
            if (note.StartTime > time) break;
            if (time < note.EndTime && !_session.GetLongNoteProgress(index).IsPopped && !_finished.Contains(index))
            {
                begin(index);
                break;
            }
        }
    }

    /// <summary>Visible long notes with their start times (for chart-order drawing).</summary>
    public IEnumerable<(TimeSpan Start, LumenSceneLayer Layer)> NoteLayers(TimeSpan time,
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
            yield return (note.StartTime, layer with
            {
                Transform = LumenMatrix.Identity with { X = head, Y = hitY, M11 = tail < head ? -1 : 1 },
            });
        }
        foreach (var index in _visible.Keys.Where(index => !retained.Contains(index)).ToArray())
            _visible.Remove(index);
    }

    public bool BalloonVisible => _balloonVisible;

    public IEnumerable<LumenPlayer> Players => _visible.Values.Select(layer => layer.Player)
        .Append(_counter.Player).Append(_balloon.Player)
        .Concat(_kusudama is null ? [] : [_kusudama.Player]);

    private void begin(int index)
    {
        _active = index;
        var note = _chart.LongNotes[index];
        if (note.Kind == PlayableLongNoteKind.Kusudama && _kusudama is not null)
        {
            _don?.SetCameraLayout(_player, DonPresentationLayout.Kusudama);
            _overlay = _kusudama;
            // Kusudama overlay (reference trace): the quota, then whether Go-Go is on.
            _sharedKusudama?.Enter(note.RequiredHits);
            call(_kusudama.Player, "AddNorma", LumenHostValue.FromNumber(note.RequiredHits));
            call(_kusudama.Player, "SetGogoTime", LumenHostValue.FromBoolean(isGoGo(note.StartTime)));
            _balloonVisible = true;
            _don?.SetMotion(new DonMotionRequest(_player, $"{_kusu}_in", $"{_kusu}_nobeat"));
        }
        else if (note.IsBalloon)
        {
            _don?.SetCameraLayout(_player, DonPresentationLayout.Standard);
            _overlay = _balloon;
            invoke(_balloon.Player, "Reset");
            // The opening timeline creates the balloon child two ticks after 'start'.
            // Prepare it before sending a nonzero quota, which also updates that child.
            invoke(_balloon.Player, "SetGekiRendaCount", 0);
            _balloon.Player.Advance();
            _balloon.Player.Advance();
            invoke(_balloon.Player, "SetGekiRendaCount", note.RequiredHits);
            _balloonVisible = true;
            _don?.SetMotion(new DonMotionRequest(_player, null, "don_balloon_nobeat"));
        }
        // Rolls: the game sends nothing at the start; the counter appears with SetRendaCount(1) on the first hit.
        _onNoteStarted?.Invoke(note.Kind);
    }

    private void hit(TaikoLongNoteProgress progress)
    {
        if (_finished.Contains(progress.NoteIndex))
            return;
        if (_active != progress.NoteIndex)
        {
            if (_active is { } previous) finish(previous);
            begin(progress.NoteIndex);
        }
        _flights?.Trigger(progress);
        if (progress.Note.IsBalloon)
        {
            if (shared(progress.NoteIndex))
            {
                // Both players' hits count down one ball; it breaks for both at zero (Update finishes it).
                var left = _sharedKusudama!.Hit();
                if (left != 0)
                    call(_kusudama!.Player, "SetCount", LumenHostValue.FromNumber(left));
                var (pump, rest) = motions(progress.Note);
                if (left != 0) _don?.SetMotion(new DonMotionRequest(_player, pump, rest));
            }
            else if (progress.IsPopped)
                finish(progress.NoteIndex);
            else
            {
                var remaining = progress.Note.RequiredHits - progress.Hits;
                if (_overlay == _kusudama)
                    call(_overlay.Player, "SetCount", LumenHostValue.FromNumber(remaining));
                else
                    invoke(_balloon.Player, "SetGekiRendaCount", remaining);
                // Every hit restarts the pumping motion, which settles back to the no-beat idle.
                var (pump, rest) = motions(progress.Note);
                _don?.SetMotion(new DonMotionRequest(_player, pump, rest));
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
            // A shared ball succeeds for both players or neither.
            var popped = shared(index) ? _sharedKusudama!.Popped : progress.IsPopped;
            if (shared(index)) _sharedKusudama!.Leave();
            if (_overlay == _kusudama)
                call(_overlay.Player, "EndResult", LumenHostValue.FromNumber(popped ? 0 : 2)); // high / miss
            else
                invoke(_balloon.Player, "GekiRendaEnd", progress.IsPopped ? 0 : 1);
            var kusudama = progress.Note.Kind == PlayableLongNoteKind.Kusudama;
            _don?.SetMotion(new DonMotionRequest(_player, (kusudama, popped) switch
            {
                (true, true) => $"{_kusu}_success02",
                (true, false) => $"{_kusu}_failure",
                (false, true) => "don_balloon_success",
                _ => "don_balloon_failure",
            }, null));
            _onBalloonCompleted?.Invoke(progress.Note.Kind, popped);
        }
        else
            invoke(_counter.Player, "RendaEnd");
        _finished.Add(index);
        _active = null;
    }

    // A kusudama both players share (two-player gameplay).
    private bool shared(int index) =>
        _sharedKusudama is not null && _chart.LongNotes[index].Kind == PlayableLongNoteKind.Kusudama;

    private (string Pump, string Idle) motions(PlayableLongNote note) =>
        note.Kind == PlayableLongNoteKind.Kusudama
            ? ($"{_kusu}_loop", $"{_kusu}_nobeat")
            : ("don_balloon_loop", "don_balloon_nobeat");

    private bool isGoGo(TimeSpan time)
    {
        var active = false;
        foreach (var point in _chart.EffectPoints)
        {
            if (point.Time > time) break;
            active = point.IsGoGo;
        }
        return active;
    }

    private static void call(LumenPlayer player, string name, LumenHostValue argument)
    {
        if (!player.TryInvokeCallback(name, [argument]))
            throw new InvalidDataException(
                $"Kusudama movie is missing callback '{name}' (has: {string.Join(", ", player.CallbackNames)}).");
    }

    private static void invoke(LumenPlayer player, string name, double? argument = null)
    {
        if (!player.TryInvokeCallback(name, argument is { } value ? [LumenHostValue.FromNumber(value)] : []))
            throw new InvalidDataException($"Long-note movie is missing callback '{name}'.");
    }
}
