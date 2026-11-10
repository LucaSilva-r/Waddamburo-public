using Waddamburo.Catalog;
using Waddamburo.Game.Lumen;

namespace Waddamburo.Game.Gameplay;

/// <summary>The immutable setup issued by a score server before a battle. A missing setup means local, unranked play.</summary>
public sealed record AiBattleSession(Guid Id, int Seed, int Version, string ChartSha256, int Course, int Options,
    int SupportLevel, bool Ranked, int Points, int Wins, double? HitMedianMs, double? HitVariance)
{
    public (double Median, double Variance)? Timing => HitMedianMs is { } median && HitVariance is { } variance
        ? (median, variance) : null;
}

/// <summary>
/// The battle model shared by live play and the score server. Decisions, timeouts and sections run on an event
/// timeline, never on render frames. At a boundary the old section closes before inputs at that exact time.
/// This is Waddamburo's versioned model, not a claim about the original game's random algorithm.
/// </summary>
public sealed class AiBattleSimulation
{
    public const int Version = 1;
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RollFrame = TimeSpan.FromTicks(171667);

    /// <summary>The first online model freezes the project's existing fallback tuning; installations can tune local play.</summary>
    public static GhostLevel OnlineLevel { get; } = new(1024, 256,
        [new(0, 0, 5), new(0, 0, -50), new(0, 0, 40)], [], 100, 5, 3, 0);

    private readonly PlayableChart _chart;
    private readonly TaikoCourse _course;
    private readonly TaikoJudgementWindows _windows;
    private readonly TaikoJudgementSession _player, _ai;
    private readonly GhostTimingModel _model;
    private readonly Random _random;
    private readonly TimeSpan[] _events, _ends;
    private readonly PriorityQueue<TaikoReplayInput, (long Time, long Order)> _pending = new();
    private readonly List<AiBattleSection> _sections = [];
    private readonly List<double> _offsets = [];
    private readonly int[,] _counts = new int[2, 4];
    private readonly int[] _rollHits;
    private int _input, _event, _note, _section, _emotion = 2;
    private long _order;
    private bool _right, _rollRight;
    private TimeSpan _lastRoll = TimeSpan.MinValue / 2;
    private TimeSpan _through = TimeSpan.MinValue;

    public AiBattleSimulation(PlayableChart chart, TaikoCourse course, int seed,
        (double Median, double Variance)? prior = null, GhostLevel? level = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (prior is { } timing && (!double.IsFinite(timing.Median) || !double.IsFinite(timing.Variance) || timing.Variance < 0))
            throw new ArgumentException("Invalid prior AI timing.", nameof(prior));
        // Providers can retain sub-microsecond timestamps and audio-tail duration; the server only has
        // canonical playable bytes. Both sides must make decisions over that exact same representation.
        chart = ChartHash.Deserialize(ChartHash.Serialize(chart, course), chart.Key.Song.Source).Chart;
        _chart = chart;
        _course = course;
        _windows = TaikoJudgementWindows.ForCourse(chart.Key.Song.Source.IsOsu() ? TaikoCourse.Oni : course);
        _player = new(chart, _windows);
        _ai = new(chart, _windows) { FollowBranch = _player.BranchAt };
        _model = new(level ?? OnlineLevel) { Prior = prior };
        _random = new BattleRandom(seed);
        _ends = AiBattleRules.SectionEnds(chart);
        _rollHits = new int[chart.LongNotes.Length];
        _events = [.. chart.HitObjects.Select(static note => note.StartTime - Lead)
            .Concat(chart.HitObjects.Select(note => note.StartTime + _windows.Miss + TimeSpan.FromTicks(1)))
            .Concat(chart.JudgedChecks.Select(static check => check.Time))
            .Concat(chart.LongNotes.Select(static note => note.EndTime))
            .Concat(_ends).Distinct().Order()];
        _player.Judged += judgement => judged(0, judgement);
        _ai.Judged += judgement => judged(1, judgement);
        _player.LongNoteHit += hit =>
        {
            var gained = hit.Hits - _rollHits[hit.NoteIndex];
            _rollHits[hit.NoteIndex] = hit.Hits;
            _counts[0, 3] += gained;
            for (var index = 0; index < gained; index++)
            {
                _lastRoll = _player.CurrentTime > _lastRoll + RollFrame ? _player.CurrentTime : _lastRoll + RollFrame;
                enqueue(new((_rollRight = !_rollRight) ? TaikoInputAction.RightDon : TaikoInputAction.LeftDon, _lastRoll));
            }
        };
    }

    public AiBattleOutcome Outcome => new([.. _sections]);
    public int Emotion => _emotion;
    public (double Median, double Variance)? Timing => GhostTimingModel.TimingOf(_offsets);
    public TimeSpan End => _ends[^1];

    /// <summary>
    /// All accepted player inputs so far, in replay order. Only events strictly before the watermark run: a new frame's
    /// first input may be clamped to the previous frame's time. Keeping that instant open makes batching irrelevant.
    /// Returns AI presses to deliver to its displayed lane, in time order.
    /// </summary>
    public IReadOnlyList<TaikoReplayInput> Advance(IReadOnlyList<TaikoReplayInput> inputs, TimeSpan through)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (through < _through || inputs.Count < _input)
            throw new ArgumentException("An AI battle cannot rewind.", nameof(through));
        var generated = new List<TaikoReplayInput>();
        while (true)
        {
            var next = _event < _events.Length ? _events[_event] : TimeSpan.MaxValue;
            if (_input < inputs.Count && inputs[_input].Time < next) next = inputs[_input].Time;
            if (_pending.TryPeek(out var pending, out _) && pending.Time < next) next = pending.Time;
            if (next >= through || next > End || next == TimeSpan.MaxValue) break;
            if (_section < _ends.Length && next == _ends[_section]) closeSection();
            _player.AdvanceTo(next);
            _ai.AdvanceTo(next);
            while (_input < inputs.Count && inputs[_input].Time == next)
            {
                var input = inputs[_input++];
                _player.SubmitInput(input.Action, input.Time);
            }
            while (_note < _chart.HitObjects.Length && _chart.HitObjects[_note].StartTime - Lead <= next)
                decide(_chart.HitObjects[_note++]);
            while (_pending.TryPeek(out pending, out _) && pending.Time == next)
            {
                _pending.Dequeue();
                _ai.SubmitInput(pending.Action, pending.Time);
                generated.Add(pending);
            }
            while (_event < _events.Length && _events[_event] == next) _event++;
        }
        _through = through;
        return generated;
    }

    private void judged(int side, TaikoNoteJudgement judgement)
    {
        if (judgement.StrongHitCompleted || judgement.Result is not { } result) return;
        _counts[side, result == TaikoHitResult.Great ? 0 : result == TaikoHitResult.Good ? 1 : 2]++;
        if (side == 0 && !judgement.TimedOut && judgement.Offset is { } offset)
        {
            _offsets.Add(offset.TotalMilliseconds);
            _model.Observe(offset);
        }
    }

    private void decide(PlayableHitObject note)
    {
        if (!PlayableChart.IsOn(note.Branch, _player.BranchAt(note.StartTime))) return;
        var player = AiBattleRules.Territory(_course, _counts[0, 0], _counts[0, 1]);
        var ai = AiBattleRules.Territory(_course, _counts[1, 0], _counts[1, 1]);
        _model.Emotion = _emotion;
        var offset = _model.NextOffset(Math.Clamp(ai - player, -9, 9), _random);
        if (offset.Duration() > _windows.Miss) return;
        // Quantize the random offset to microseconds, the canonical chart's time precision.
        var time = note.StartTime + TimeSpan.FromTicks((long)Math.Round(offset.Ticks / 10d) * 10);
        var ka = note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;
        _right = !note.IsStrong && !_right;
        enqueue(new((ka, _right) switch
        {
            (true, false) => TaikoInputAction.LeftKa,
            (true, true) => TaikoInputAction.RightKa,
            (false, false) => TaikoInputAction.LeftDon,
            _ => TaikoInputAction.RightDon,
        }, time));
        if (note.IsStrong) enqueue(new(ka || note.IsKadon ? TaikoInputAction.RightKa : TaikoInputAction.RightDon, time));
    }

    private void enqueue(TaikoReplayInput input) => _pending.Enqueue(input, (input.Time.Ticks, _order++));

    private void closeSection()
    {
        var won = AiBattleRules.Territory(_course, _counts[0, 0], _counts[0, 1])
            >= AiBattleRules.Territory(_course, _counts[1, 0], _counts[1, 1]);
        AiSectionCounts counts(int side) => new(_counts[side, 0], _counts[side, 1], _counts[side, 2], _counts[0, 3]);
        _sections.Add(new(won, counts(0), counts(1)));
        var lead = _sections.Count(static section => section.Won) * 2 - _sections.Count;
        _emotion = lead >= 2 ? 4 : lead <= -2 ? 0 : _emotion;
        Array.Clear(_counts);
        _section++;
    }

    /// <summary>A pinned xorshift32 stream; independent of System.Random's runtime-specific implementation.</summary>
    private sealed class BattleRandom(int seed) : Random
    {
        private uint _state = unchecked((uint)seed ^ 0x9e3779b9u) is var state and not 0 ? state : 1;
        private uint draw()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
        public override double NextDouble() => draw() / 4294967296d;
        public override int Next(int maxValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
            return maxValue == 0 ? 0 : (int)(draw() % (uint)maxValue);
        }
    }
}
