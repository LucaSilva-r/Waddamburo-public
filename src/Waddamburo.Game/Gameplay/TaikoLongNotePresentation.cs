using Waddamburo.Catalog;
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

/// <summary>
/// What a long note's own movie is told beyond the provider: the game's are driven by callbacks and labels
/// (Waddamburo.Skins.Green); ours read note.length and note.hit and need none of this.
/// </summary>
public interface ITaikoLongNoteMovies
{
    /// <summary>A long note's movie was made for a note coming on screen.</summary>
    void Shown(LumenPlayer note);

    /// <summary>A roll's body or text, <paramref name="length"/> pixels from head to tail this tick.</summary>
    void Sized(LumenPlayer note, float length);

    /// <summary>After each frame a long note's movie plays (and once a tick when it stands still).</summary>
    void Stepped(LumenPlayer note);

    /// <summary>A hit on the roll this movie draws.</summary>
    void Struck(LumenPlayer note);
}

/// <summary>
/// What the lane's character does on a balloon or kusudama: the game's Don takes their poses
/// (Waddamburo.Skins.Green); ours hear balloon.started, play.balloon and balloon.ended.
/// </summary>
public interface ITaikoLongNoteCharacter
{
    /// <summary>The overlay opened: <paramref name="overlay"/> is Kusudama for the kusudama movie, else Balloon.</summary>
    void Opened(PlayableLongNoteKind overlay);

    /// <summary>A hit on the balloon or kusudama that leaves it standing.</summary>
    void Pumped(PlayableLongNote note);

    /// <summary>The balloon or kusudama is over: burst (for a shared kusudama, by both drums) or not.</summary>
    void Closed(PlayableLongNote note, bool popped);
}

/// <summary>Owns isolated authored roll movies and the active long-note counters.</summary>
public sealed class TaikoLongNotePresentation
{
    private readonly PlayableChart _chart;
    private readonly ITaikoJudgementSource _session;
    private readonly Func<PlayableLongNoteKind, LumenSceneLayer> _createNote;
    private readonly Dictionary<int, LumenSceneLayer> _visible = [];
    private readonly Func<PlayableLongNoteKind, LumenSceneLayer>? _createText;
    private readonly Dictionary<int, LumenSceneLayer> _texts = []; // roll text, sized like the body
    private readonly LumenSceneLayer _counter;
    private readonly LumenSceneLayer _balloon;
    /// <summary>The lane's character, told of balloons and kusudama (set once it exists).</summary>
    public ITaikoLongNoteCharacter? Character { get; set; }
    private readonly ITaikoLongNoteMovies? _movies;

    /// <summary>A hit on a long note the lane shows (its flight, for the game's flight movies).</summary>
    public event Action<TaikoLongNoteProgress>? Struck;
    private readonly Action<PlayableLongNoteKind, bool>? _onBalloonCompleted;
    private readonly Action<PlayableLongNoteKind>? _onNoteStarted;
    private int? _active;

    /// <summary>The roll or balloon under way: a roll's hits so far, or the hits a balloon still needs.</summary>
    public (bool Balloon, bool Kusudama, int Count)? Active => _active is not { } index ? null
        : _chart.LongNotes[index].IsBalloon
            ? (true, _chart.LongNotes[index].Kind == PlayableLongNoteKind.Kusudama, shared(index) ? _sharedKusudama!.Remaining
                : Math.Max(_chart.LongNotes[index].RequiredHits - _session.GetLongNoteProgress(index).Hits, 0))
            : (false, false, _shownHits);
    private readonly HashSet<int> _finished = []; // a shared ball can end before the lane's own quota
    private bool _balloonVisible;
    private readonly LumenSceneLayer? _kusudama;
    private readonly TaikoSharedKusudama? _sharedKusudama;
    private LumenSceneLayer _overlay; // the balloon or kusudama overlay in use

    /// <summary>
    /// A balloon or kusudama came under way, with the hits it needs and whether it is in Go-Go time (by the chart, at
    /// its start): the kind is the overlay shown (a kusudama without a kusudama movie is shown as a balloon). Rolls
    /// start with their first hit, told by <see cref="Changed"/>.
    /// </summary>
    public event Action<PlayableLongNoteKind, int, bool>? Started;

    /// <summary>A balloon or kusudama ended (the kind as <see cref="Started"/>): burst, or not.</summary>
    public event Action<PlayableLongNoteKind, bool>? Ended;

    /// <summary><see cref="Active"/> may have changed: a hit, a start or an end.</summary>
    public event Action? Changed;


    /// <summary>The roll count a roll shows (by long note index); null: this lane's own hits. AI battle's AI-don shows
    /// the player's (its rolls copy theirs).</summary>
    public Func<int, int>? ShownHits { get; set; }

    private int _shownHits;

    // A roll's count as shown; with ShownHits, told whenever it changes (not only on this lane's own hits: AI-don's
    // last copy of a hit can come after the roll ended). Returns the count.
    private int refreshShownHits(int index)
    {
        var hits = ShownHits?.Invoke(index) ?? _session.GetLongNoteProgress(index).Hits;
        var changed = ShownHits is not null && hits > 0 && hits != _shownHits;
        _shownHits = hits;
        if (changed) Changed?.Invoke();
        return hits;
    }

    public TaikoLongNotePresentation(PlayableChart chart, ITaikoJudgementSource session,
        Func<PlayableLongNoteKind, LumenSceneLayer> createNote, LumenSceneLayer counter,
        LumenSceneLayer balloon,
        LumenSceneLayer? kusudama = null, Action<PlayableLongNoteKind, bool>? onBalloonCompleted = null,
        Action<PlayableLongNoteKind>? onNoteStarted = null, TaikoSharedKusudama? sharedKusudama = null,
        Func<PlayableLongNoteKind, LumenSceneLayer>? createText = null, ITaikoLongNoteMovies? movies = null)
    {
        _movies = movies;
        _createText = createText;

        _sharedKusudama = sharedKusudama;
        kusudama ??= sharedKusudama?.Layer;
        _chart = chart;
        _session = session;
        _createNote = createNote;
        _counter = counter;
        _balloon = balloon;
        _overlay = balloon;
        _kusudama = kusudama;
        _onBalloonCompleted = onBalloonCompleted;
        _onNoteStarted = onNoteStarted;
        session.LongNoteHit += hit;
    }

    /// <summary>Applies the combo face to every long note on screen.</summary>
    public void ShowFaces(Action<LumenPlayer> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        foreach (var layer in _visible.Values)
            show(layer.Player);
    }

    /// <remarks>
    /// <paramref name="steps"/>: how many frames each long note's movie plays on this tick (its face kept at the
    /// beat's phase: none while a reviewed play stands still, more when it runs fast); null: one.
    /// </remarks>
    public void AdvanceAnimations(Func<LumenPlayer, int>? steps = null)
    {
        if (_active is { } rolling && !_chart.LongNotes[rolling].IsBalloon)
            refreshShownHits(rolling);
        foreach (var layer in _visible.Values)
        {
            // Each frame played is followed by Stepped (the game's lay their tail out there); a still note
            // still gets one a tick.
            var frames = steps?.Invoke(layer.Player) ?? 1;
            for (var frame = 0; frame < Math.Max(1, frames); frame++)
            {
                if (frame < frames)
                    layer.Player.Advance();
                _movies?.Stepped(layer.Player);
            }
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
            if (IsHeld(index, time) || !onRoute(index)) continue; // held: opened by its first hit
            if (time < note.EndTime && !_session.GetLongNoteProgress(index).IsPopped && !_finished.Contains(index))
            {
                begin(index);
                break;
            }
        }
    }

    private bool onRoute(int index) =>
        PlayableChart.IsOn(_chart.LongNotes[index].Branch, _session.BranchAt(_chart.LongNotes[index].StartTime));

    /// <summary>Visible long notes with their start times (for chart-order drawing); <paramref name="bodies"/> false: their text only (ドロン).</summary>
    public IEnumerable<(TimeSpan Start, LumenSceneLayer Layer)> NoteLayers(TimeSpan time,
        Func<TimeSpan, TimeSpan, ChartBranch?, (float X, float Y)> position, (float X, float Y) target, bool bodies = true)
    {
        var retained = new HashSet<int>();
        for (var index = _chart.LongNotes.Length - 1; index >= 0; index--)
        {
            var note = _chart.LongNotes[index];
            if (!onRoute(index)) continue;
            // Rolls remain on screen after their scoring window closes until the tail passes left.
            // A balloon left unplayed slides off from the target when its time runs out (user-confirmed on the game).
            var unplayed = note.Kind == PlayableLongNoteKind.Balloon && time >= note.EndTime
                && _session.GetLongNoteProgress(index).Hits == 0;
            if (note.IsBalloon && !unplayed && (time >= note.EndTime || _session.GetLongNoteProgress(index).IsPopped)) continue;
            var held = IsHeld(index, time);
            if (note.IsBalloon && time >= note.StartTime && !held && !unplayed) continue;
            var head = held ? target : position(unplayed ? note.EndTime : note.StartTime, note.StartTime, note.Branch);
            var tail = note.IsBalloon ? head : position(note.EndTime, note.StartTime, note.Branch);
            if (Math.Max(head.X, tail.X) < -100 || Math.Min(head.X, tail.X) > 1380
                || Math.Max(head.Y, tail.Y) < -100 || Math.Min(head.Y, tail.Y) > 820) continue;
            var (dx, dy) = (tail.X - head.X, tail.Y - head.Y);
            var length = MathF.Sqrt(dx * dx + dy * dy);
            retained.Add(index);
            if (!_visible.TryGetValue(index, out var layer))
            {
                layer = _createNote(note.Kind);
                _movies?.Shown(layer.Player);
                _visible.Add(index, layer);
            }
            // Along the roll, head to tail: mirrored when it runs left, turned when it slants (TJA complex scroll).
            var transform = dy == 0 || length == 0
                ? LumenMatrix.Identity with { X = head.X, Y = head.Y, M11 = dx < 0 ? -1 : 1 }
                : new LumenMatrix(dx / length, dy / length, -dy / length, dx / length, head.X, head.Y);
            if (!note.IsBalloon)
            {
                layer.Player.Publish("note.length", LumenHostValue.FromNumber(length));
                _movies?.Sized(layer.Player, length);
                // Drawn as set, never blended from the last tick: the width follows the roll's place, which
                // is exact every frame (a stale blend stretched the tail, worst when scrubbing a review back).
                layer.Player.HoldStill();
            }
            if (bodies)
                yield return (note.StartTime, layer with { Transform = transform });
            // Traced: a roll spawns onp_renda(_dai)_moji below it, sized with the same SetWidth.
            if (note.IsBalloon || _createText is null) continue;
            if (!_texts.TryGetValue(index, out var text))
                _texts.Add(index, text = _createText(note.Kind));
            text.Player.Publish("note.length", LumenHostValue.FromNumber(length));
            _movies?.Sized(text.Player, length);
            text.Player.HoldStill();
            yield return (note.StartTime, text with { Transform = transform with { Y = head.Y + TaikoNoteText.Offset } });
        }
        foreach (var index in _visible.Keys.Where(index => !retained.Contains(index)).ToArray())
        {
            _visible.Remove(index);
            _texts.Remove(index);
        }
    }

    public bool BalloonVisible => _balloonVisible;

    /// <summary>
    /// A balloon waits on the target, its overlay closed, until the first don opens it (traced
    /// session25: the overlay appeared with the quota minus that hit, and only don_balloon_nobeat).
    /// </summary>
    // ponytail: kusudamas still open on time; untraced, extend if a trace shows they wait too.
    public bool IsHeld(int index, TimeSpan time)
    {
        var note = _chart.LongNotes[index];
        return note.Kind == PlayableLongNoteKind.Balloon && time >= note.StartTime && time < note.EndTime
            && _session.GetLongNoteProgress(index).Hits == 0;
    }

    /// <summary>The overlays drawn at the hit target (balloon, roll counter), which follow its #JPOSSCROLL moves; not the kusudama.</summary>
    public IEnumerable<LumenPlayer> TargetOverlays => [_counter.Player, _balloon.Player];

    /// <summary>The balloon overlay and the kusudama (when the lane has one): the character's poses show in them.</summary>
    public (LumenPlayer Balloon, LumenPlayer? Kusudama) Overlays => (_balloon.Player, _kusudama?.Player);

    public IEnumerable<LumenPlayer> Players => _visible.Values.Select(layer => layer.Player)
        .Append(_counter.Player).Append(_balloon.Player)
        .Concat(_kusudama is null ? [] : [_kusudama.Player]);

    private void begin(int index)
    {
        _active = index;
        _shownHits = 0;
        var note = _chart.LongNotes[index];
        if (note.Kind == PlayableLongNoteKind.Kusudama && _kusudama is not null)
        {
            _overlay = _kusudama;
            _sharedKusudama?.Enter(note.RequiredHits);
            Started?.Invoke(PlayableLongNoteKind.Kusudama, note.RequiredHits, _chart.IsGoGoAt(note.StartTime, note.Branch));
            Changed?.Invoke();
            _balloonVisible = true;
            Character?.Opened(PlayableLongNoteKind.Kusudama);
        }
        else if (note.IsBalloon)
        {
            _overlay = _balloon;
            Started?.Invoke(PlayableLongNoteKind.Balloon, note.RequiredHits, _chart.IsGoGoAt(note.StartTime, note.Branch));
            Changed?.Invoke();
            _balloonVisible = true;
            Character?.Opened(PlayableLongNoteKind.Balloon);
        }
        // Rolls: nothing at the start; the count appears with the first hit.
        _onNoteStarted?.Invoke(note.Kind);
    }

    private void hit(TaikoLongNoteProgress progress)
    {
        if (_finished.Contains(progress.NoteIndex))
            return;
        var opened = _active != progress.NoteIndex;
        if (opened)
        {
            if (_active is { } previous) finish(previous);
            begin(progress.NoteIndex);
        }
        Struck?.Invoke(progress);
        if (progress.Note.IsBalloon)
        {
            if (shared(progress.NoteIndex))
            {
                // Both players' hits count down one ball; it breaks for both at zero (Update finishes it).
                var left = _sharedKusudama!.Hit();
                Changed?.Invoke();
                if (left != 0) Character?.Pumped(progress.Note);
            }
            else if (progress.IsPopped)
                finish(progress.NoteIndex);
            else if (!opened || progress.Note.Kind != PlayableLongNoteKind.Balloon) // begin showed the opening hit
            {
                Changed?.Invoke();
                Character?.Pumped(progress.Note);
            }
        }
        else
        {
            _shownHits = ShownHits?.Invoke(progress.NoteIndex) ?? progress.Hits;
            Changed?.Invoke();
            if (_visible.TryGetValue(progress.NoteIndex, out var layer))
            {
                layer.Player.Emit("note.hit", LumenHostValue.FromString(
                    progress.LastAction is TaikoInputAction.LeftDon or TaikoInputAction.RightDon ? "don" : "ka"));
                _movies?.Struck(layer.Player);
            }
        }
    }

    /// <summary>
    /// Closes the roll, balloon or kusudama under way, as the movies' own ends do but without Don's
    /// reaction or the completion cue: a reviewed play going back drops these lanes, and the overlays
    /// belong to the scene, so they must not stay open.
    /// </summary>
    public void Abandon()
    {
        if (_active is not { } index)
            return;
        if (_chart.LongNotes[index].IsBalloon)
            Ended?.Invoke(overlayKind, false);
        _finished.Add(index);
        _active = null;
        Changed?.Invoke();
    }

    private void finish(int index)
    {
        var progress = _session.GetLongNoteProgress(index);
        if (progress.Note.IsBalloon)
        {
            // A shared ball succeeds for both players or neither.
            var popped = shared(index) ? _sharedKusudama!.Popped : progress.IsPopped;
            if (shared(index)) _sharedKusudama!.Leave();
            Ended?.Invoke(overlayKind, _overlay == _kusudama ? popped : progress.IsPopped);
            Character?.Closed(progress.Note, popped);
            _onBalloonCompleted?.Invoke(progress.Note.Kind, popped);
        }
        else
            refreshShownHits(index);
        _finished.Add(index);
        _active = null;
        Changed?.Invoke();
    }

    private PlayableLongNoteKind overlayKind =>
        _overlay == _kusudama ? PlayableLongNoteKind.Kusudama : PlayableLongNoteKind.Balloon;

    // A kusudama both players share (two-player gameplay).
    private bool shared(int index) =>
        _sharedKusudama is not null && _chart.LongNotes[index].Kind == PlayableLongNoteKind.Kusudama;

}
