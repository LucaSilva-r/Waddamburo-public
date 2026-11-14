using System.Collections.Immutable;
using Waddamburo.Catalog;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Authored presentation driven by native judgement; no asset loading or audio.</summary>
public sealed class TaikoLumenPresentation
{
    private PlayableChart _chart;
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
    private readonly LumenSceneLayer _target;
    private readonly LumenPlayer _feedback;
    private readonly LumenPlayer _board;
    private readonly float _hitX;
    private readonly float _hitY;
    private readonly float _hitRadius;
    private readonly TaikoGoGoPresentation _gogo;
    private readonly Func<IEnumerable<LumenSceneLayer>>? _flights; // the hit flights drawn now
    private readonly Func<LumenPlayer, bool> _flies; // a flight movie: placed with the target
    private readonly TaikoLongNotePresentation? _longNotes;

    public TaikoLumenPresentation(PlayableChart chart, ITaikoJudgementSource judgement,
        IEnumerable<LumenSceneLayer> background, IEnumerable<LumenSceneLayer> foreground,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer> notes,
        LumenSceneLayer bar, LumenSceneLayer target, LumenPlayer feedback, LumenPlayer board,
        Func<IEnumerable<LumenSceneLayer>>? flights = null, TaikoLongNotePresentation? longNotes = null, int? flightsAt = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? handNotes = null,
        IReadOnlyDictionary<PlayableNoteKind, LumenSceneLayer>? synchroNotes = null,
        LumenSceneLayer? rareNote = null,
        LumenSceneLayer? kadonNote = null,
        IReadOnlyDictionary<string, LumenSceneLayer>? noteTexts = null,
        LumenSceneLayer? branchBar = null,
        LumenPlayer? branchBubble = null,
        IReadOnlyDictionary<LumenPlayer, string>? roles = null,
        Func<LumenPlayer, bool>? flies = null)
    {
        _flies = flies ?? (static _ => false);
        _branchBar = branchBar;
        _ = branchBubble; // the game's bubble is jumped by its adapter (BranchChanged)
        _checkBars = [.. chart.Branching?.Checks.Select(static check => check.Start) ?? []];
        _noteTexts = noteTexts;
        _noteLabels = TaikoNoteText.Labels(chart);
        _rareNote = rareNote;
        _kadonNote = kadonNote;
        _roles = roles;
        _handNotes = handNotes;
        _synchroNotes = synchroNotes;
        _chart = chart;
        _judgement = judgement;
        _background = background.ToArray();
        _foreground = foreground.ToArray();
        _scenePlayers = [.. _background.Concat(_foreground).Select(static layer => layer.Player)];
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
            GoGoChanged?.Invoke(active);
        });
        // Lanes built again on the same movie (practice retries, replay jumps): the target back in its place first.
        foreach (var clip in TargetClips)
            target.Player.SetInstanceOffset(clip, 0, 0);
        if (!target.Player.TryGetInstanceBounds("target", out var bounds))
            throw new InvalidDataException("Gameplay lane is missing rendered target geometry.");
        (_hitX, _hitY) = target.Transform.Transform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _hitRadius = _hitY - target.Transform.Transform(bounds.X + bounds.Width / 2, bounds.Y).Y;
        _mods = new ChartModValues(chart.Effects.Mods);
        _geometry = new NoteGeometry(chart, _hitX, _hitY, _mods);
        _layout = new ChartModValues(chart.Effects.Layout);
        _stage = new StageEffects(_mods, _geometry, targetMoves: !chart.Effects.JudgeMoves.IsEmpty, _layout);
        StageEffects.Prime(_background.Concat(_foreground));
        judgement.Judged += onJudged;
    }

    /// <summary>Replaces drawing effects without rebuilding judgement, movie players, or the live attempt.</summary>
    public void ReplaceEffects(ChartEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var chart = _chart with { Effects = effects };
        var mods = new ChartModValues(effects.Mods);
        var geometry = new NoteGeometry(chart, _hitX, _hitY, mods);
        _layout = new ChartModValues(effects.Layout);
        var stage = new StageEffects(mods, geometry, targetMoves: !effects.JudgeMoves.IsEmpty, _layout);
        _chart = chart;
        _mods = mods;
        _geometry = geometry;
        _stage = stage;
        _noteWindows = null;
        _barStreams = null;
        // Removing judge moves must also restore offsets already written into the target movie.
        _moved = default;
        offsetTarget(0, 0);
    }

    /// <summary>The drum was struck (whether or not a note was there).</summary>
    public event Action<TaikoInputAction>? DrumHit;

    public void Hit(TaikoInputAction action) => DrumHit?.Invoke(action);

    public event Action<bool>? GoGoChanged;

    /// <summary>The route the lane shows changed (a branching chart only).</summary>
    public event Action<ChartBranch>? BranchChanged;

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
        if (_chart.Effects.JudgeMoves.IsEmpty) return;
        // Every tick, not only on a change: a clip the timeline places again (the fire at each Go-Go) comes back unmoved.
        _moved = _geometry.JudgeOffset(time);
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

    /// <summary>Where the playfield is drawn at <paramref name="time"/> (effects, layout): for parts the host draws on it.</summary>
    public LumenMatrix PlayfieldPlacement(TimeSpan time) => _stage.Matrix("playfield", _geometry.Origin(time), time);

    public void Update(TimeSpan time)
    {
        _longNotes?.Update(time);
        _gogo.Update(time);
        moveTarget(time);
        double turn(string axis) => _mods.Value("don", axis, time) + _layout.Value("don", axis, time);
        DonTurn?.Invoke(turn("yaw"), turn("pitch"), turn("roll"));
        // The lane takes the route a check picked at once (lane_hit plays its normal_kurouto etc. transition),
        // and Don-chan's bubble says which way it went (traced: same tick; the movie stops itself after).
        if (_judgement.Branch != _shownBranch)
        {
            _shownBranch = _judgement.Branch;
            BranchChanged?.Invoke(_shownBranch);
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

    /// <summary>Scene bounds for the effects editor; evaluated only while the workspace requests them.</summary>
    public IReadOnlyList<StageEffectElement> EffectElements(TimeSpan time, LumenSceneLayer? character = null, int characterSlot = 0)
    {
        var layers = new List<LumenSceneLayer>(_background);
        if (character is not null) layers.Insert(Math.Clamp(characterSlot, 0, layers.Count), character);
        layers.AddRange(_foreground);
        var elements = new List<StageEffectElement>();
        foreach (var layer in layers.Concat(_overlays.Values))
        {
            var target = _overlays.FirstOrDefault(pair => pair.Value == layer).Key ?? targetOf(layer, character);
            if (target is null || _stage.Describe(layer, target, followsTarget(layer), time) is not { } element) continue;
            var previous = elements.FindIndex(e => e.Target == target);
            if (previous >= 0)
            {
                var old = elements[previous];
                element = element with { Minimum = System.Numerics.Vector2.Min(old.Minimum, element.Minimum),
                    Maximum = System.Numerics.Vector2.Max(old.Maximum, element.Maximum) };
                elements.RemoveAt(previous);
            }
            elements.Add(element);
        }
        return elements;
    }

    /// <summary>
    /// The lane's layers in draw order, split at the notes: backgrounds, lane, bar lines and notes;
    /// then the overlays in front (two lanes are merged back, back, front, front).
    /// </summary>
    // Notes taken off the lane without a judgement (the audio calibration's hit notes).
    private readonly HashSet<int> _removed = [];

    /// <summary>Stops drawing note <paramref name="index"/>, with no judgement shown.</summary>
    public void RemoveNote(int index) => _removed.Add(index);

    // Movies the host draws over the lane that a layout still places (the resume countdown, "timer"), by target.
    private readonly Dictionary<string, LumenSceneLayer> _overlays = new(StringComparer.Ordinal);

    /// <summary>Registers a host-drawn movie as <paramref name="target"/>'s element (the editor can select it).</summary>
    public void SetOverlay(string target, LumenSceneLayer layer) => _overlays[target] = layer;

    /// <summary>A host-drawn movie where the effects put <paramref name="target"/> at <paramref name="time"/>.</summary>
    public LumenSceneLayer PlaceOverlay(LumenSceneLayer layer, string target, TimeSpan time) =>
        _stage.IsActive ? _stage.Place(layer, target, false, time) : layer;

    /// <summary>A movie placed as the combo announcement draws nothing now (the layout editor shows it again).</summary>
    public bool ComboHidden => (_roles ?? new Dictionary<LumenPlayer, string>()).Any(static pair => StageEffects.TargetOf(pair.Value) == "combo"
        && !pair.Key.CreateRenderSnapshot().Quads.Any(static quad => quad.MultiplyColor.Alpha > 0));

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
                    addAt(layers, _branchBar is { } yellow && _checkBars.Contains(bar.Time) ? yellow : _bar, bar.Time, time, at, barLine: true);
            }
        // Notes of every kind in chart order, later ones behind (the game gives each spawned note a
        // deeper depth), so an earlier roll body covers the notes that follow it.
        var notes = new List<(TimeSpan Start, LumenSceneLayer Layer)>();
        if (_longNotes is not null)
            notes.AddRange(_longNotes.NoteLayers(time,
                (noteTime, controlTime, route) => _geometry.Point(noteTime, time, controlTime, route), _geometry.Origin(time), bodies: !Doron));
        var hitNotes = new List<LumenSceneLayer>(1);
        var texts = new List<LumenSceneLayer>();
        var noteWindows = _noteWindows ??= [.. _chart.HitObjects.Select(note => _geometry.Window(note.StartTime, note.Branch, NoteGeometry.ScrollTime(note)))];
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
            var control = NoteGeometry.ScrollTime(note);
            addAt(hitNotes, template, note.StartTime, time, note.Branch, hitNote: true, controlTime: control);
            if (hitNotes.Count != 0 && !Doron) notes.Add((_chart.HitObjects[index].StartTime, hitNotes[0]));
            // Traced: each note spawns an onp_moji clone (ドン/カッ label) 82 px below it that scrolls with it.
            if (_noteTexts?.GetValueOrDefault(_noteLabels[index]) is { } text)
                addAt(texts, text, note.StartTime, time, note.Branch, TaikoNoteText.Offset, hitNote: true, controlTime: control);
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
            front.AddRange(_flights());
        front.AddRange(_foreground.Skip(_flightsAt));
        if (!_stage.IsActive)
            return (layers, front);
        if (_mods.TouchesProperty("depth") || _layout.TouchesProperty("depth"))
        {
            // Elements by depth (a stable sort: the game's order among equals), all in one list: front is left empty.
            // ponytail: with two lanes, a lane with depths no longer interleaves its front behind the other lane's.
            var all = layers.Concat(front).ToArray();
            var depths = all.Select(layer => targetOf(layer, character) is { } target ? Math.Round(_mods.Value(target, "depth", time) + _layout.Value(target, "depth", time)) : 0).ToArray();
            return ([.. Enumerable.Range(0, all.Length).OrderBy(i => depths[i]).Select(i => place(all[i], character, time))], []);
        }
        return ([.. layers.Select(layer => place(layer, character, time))], [.. front.Select(layer => place(layer, character, time))]);
    }

    // A layer where the chart's effects put it: its modchart element (Don-chan, a scene role, or the playfield for
    // everything drawn on the lane) and whether it sits at the target (judgement text, roll counter, balloon, flights:
    // OpenTaiko keeps those where they are relative to the moved target, a flight's landing included).
    private LumenSceneLayer place(LumenSceneLayer layer, LumenSceneLayer? character, TimeSpan time)
    {
        return _stage.Place(layer, targetOf(layer, character), followsTarget(layer), time);
    }

    private string? targetOf(LumenSceneLayer layer, LumenSceneLayer? character) =>
        character is not null && ReferenceEquals(layer, character) ? "don"
            : _scenePlayers.Contains(layer.Player) ? StageEffects.TargetOf(_roles?.GetValueOrDefault(layer.Player))
            : "playfield";

    private bool followsTarget(LumenSceneLayer layer) => layer.Player == _feedback || _flies(layer.Player)
            || _longNotes?.TargetOverlays.Contains(layer.Player) == true;

    private void addAt(List<LumenSceneLayer> layers, LumenSceneLayer template,
        TimeSpan noteTime, TimeSpan time, ChartBranch? route, float yOffset = 0, bool hitNote = false, bool barLine = false,
        TimeSpan? controlTime = null)
    {
        if (_geometry.Place(noteTime, time, route, yOffset, hitNote, barLine, controlTime) is { } placed)
            layers.Add(template with { Transform = placed.Transform, Alpha = placed.Alpha });
    }

    // When each note and bar is on the lane, so a frame skips the rest of a chart with one comparison each.
    // ponytail: still a pass over every note, a cursor needs care as scroll changes reorder arrivals.
    private (TimeSpan From, TimeSpan Until)[]? _noteWindows;
    private (ChartBranch? Route, (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] Bars)[]? _barStreams;
    private (ChartBarLine Bar, TimeSpan From, TimeSpan Until)[] barTimes(ImmutableArray<ChartBarLine> bars) =>
        [.. bars.Select(bar => _geometry.BarWindow(bar.Time) is var window ? (bar, window.From, window.Until) : default)];

    /// <summary>
    /// A note this lane shows judged by a hit (not one that passed unhit, nor a big note's second half): the lane's
    /// hit flash and judgement word. From the stream the lane draws, which for an online partner's lane is their
    /// live play (note.judged follows the lane's own count).
    /// </summary>
    public event Action<TaikoNoteJudgement>? Shown;

    private void onJudged(TaikoNoteJudgement result)
    {
        if (result.StrongHitCompleted || result.Placeholder || result.TimedOut)
            return;
        Shown?.Invoke(result);
    }

    // The chart's effects (ChartEffects): note placement, and the scene's movies under modcharts and target moves.
    private ChartModValues _mods;
    private ChartModValues _layout = new([]); // the player's 4:3 layout under the chart's modchart
    private NoteGeometry _geometry;
    private StageEffects _stage;
    private readonly IReadOnlyDictionary<LumenPlayer, string>? _roles;
    private readonly HashSet<LumenPlayer> _scenePlayers;

    /// <summary>Called each <see cref="Update"/> with Don-chan's model turn (yaw, pitch, roll in degrees).</summary>
    public Action<double, double, double>? DonTurn { get; set; }

    private static void callback(LumenPlayer player, string name, LumenHostValue value)
    {
        if (!player.TryInvokeCallback(name, [value]))
            throw new InvalidDataException($"Gameplay movie is missing callback '{name}'.");
    }
}
