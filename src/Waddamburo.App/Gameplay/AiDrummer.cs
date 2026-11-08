using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// AI-don's drumming in an AI battle (lane 1, the second drum's keys): a ghost of the player. Each note is timed a little
/// before it comes by <see cref="GhostTimingModel"/> (the player's own timing, rubber-banded by the lead); the judgement
/// is the lane's own. Rolls and balloons copy the player's hits exactly (user-confirmed, and traced: equal roll counts).
/// </summary>
internal sealed class AiDrummer
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(200);

    private readonly PlayableHitObject[] _notes;
    private readonly TaikoJudgementWindows _windows;
    private readonly GhostTimingModel _model;
    private readonly Func<(int Player, int Ai)> _territory;
    private readonly Func<int> _playerRolls;
    private readonly Random _random;
    private readonly List<(TimeSpan Time, SdlKeyboardKey Key)> _pending = [];
    private int _nextNote, _rolls;
    private bool _right, _rollRight;

    /// <param name="territory">This section's territory points, the player's and AI-don's.</param>
    /// <param name="playerRolls">The player's roll and balloon hits so far.</param>
    public AiDrummer(PlayableChart chart, TaikoCourse course, int seed, GhostTimingModel model,
        Func<(int Player, int Ai)> territory, Func<int> playerRolls)
    {
        chart = chart.OnRoute(ChartBranch.Master);
        _notes = [.. chart.HitObjects];
        _windows = TaikoJudgementWindows.ForCourse(chart.Key.Song.Source == SongSourceKind.OsuLazer ? TaikoCourse.Oni : course);
        _model = model;
        _territory = territory;
        _playerRolls = playerRolls;
        _random = new Random(seed);
    }

    /// <summary>One of the player's judged hits (AI-don learns their timing).</summary>
    public void Observe(TaikoNoteJudgement judgement)
    {
        if (!judgement.StrongHitCompleted && !judgement.TimedOut && judgement.Offset is { } offset && judgement.Result is not null)
            _model.Observe(offset);
    }

    public SdlKeyboardSnapshot Apply(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        while (_nextNote < _notes.Length && _notes[_nextNote].StartTime <= chartTime + Lead)
            decide(_notes[_nextNote++]);
        // Rolls and balloons: a hit for each of the player's, now.
        for (var rolls = _playerRolls(); _rolls < rolls; _rolls++)
            _pending.Add((chartTime, key((_rollRight = !_rollRight) ? TaikoInputAction.RightDon : TaikoInputAction.LeftDon)));
        if (!_pending.Any(press => press.Time <= chartTime))
            return keyboard;
        var presses = keyboard.Presses.ToList();
        foreach (var press in _pending.Where(press => press.Time <= chartTime).OrderBy(static press => press.Time))
        {
            var timestamp = keyboard.Timestamp - (chartTime - press.Time);
            presses.Add(new SdlKeyPress(press.Key, timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp));
        }
        _pending.RemoveAll(press => press.Time <= chartTime);
        return new SdlKeyboardSnapshot(keyboard.PressedKeys, presses, keyboard.Timestamp);
    }

    private void decide(PlayableHitObject note)
    {
        // AI-don's lead this section, in 良.
        var (player, ai) = _territory();
        var offset = _model.NextOffset((ai - player) / (double)Math.Max(1, _model.Level.ScoreGreat), _random);
        if (offset.Duration() > _windows.Miss)
            return; // past the 不可 window: no hit, a miss
        var time = note.StartTime + offset;
        var ka = note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa;
        _right = !note.IsStrong && !_right;
        _pending.Add((time, key((ka, _right) switch
        {
            (true, false) => TaikoInputAction.LeftKa,
            (true, true) => TaikoInputAction.RightKa,
            (false, false) => TaikoInputAction.LeftDon,
            _ => TaikoInputAction.RightDon,
        })));
        if (note.IsStrong)
            _pending.Add((time, key(ka || note.IsKadon ? TaikoInputAction.RightKa : TaikoInputAction.RightDon)));
    }

    // The second drum's keys.
    private static SdlKeyboardKey key(TaikoInputAction action) => action switch
    {
        TaikoInputAction.LeftDon => SdlKeyboardKey.X,
        TaikoInputAction.RightDon => SdlKeyboardKey.C,
        TaikoInputAction.LeftKa => SdlKeyboardKey.Z,
        _ => SdlKeyboardKey.V,
    };
}
