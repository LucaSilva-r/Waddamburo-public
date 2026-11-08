using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// AI-don's drumming in an AI battle (lane 1, the second drum's keys). Each note is decided a little before it comes:
/// 良, 可 or a miss, leaning to keep the section close (taiko-fumen wiki: the AI keeps the battle close in real time;
/// its rolls match the player's, so they decide nothing). A 良 lands on the note, a 可 between the 良 and 可 windows.
/// </summary>
/// <remarks>
/// ponytail: a closeness heuristic, not Green's model (ghosttuning.xml: the player's timing median/variance, per-level
/// patterns, mood); its numbers are to be tuned against plays.
/// </remarks>
internal sealed class AiDrummer
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RollInterval = TimeSpan.FromSeconds(1 / 15d);

    private readonly PlayableHitObject[] _notes;
    private readonly (TimeSpan Time, SdlKeyboardKey Key)[] _rolls;
    private readonly TaikoJudgementWindows _windows;
    private readonly int _greatWeight;
    private readonly Func<(int Player, int Ai)> _territory;
    private readonly Random _random;
    private readonly List<(TimeSpan Time, SdlKeyboardKey Key)> _pending = [];
    private int _nextNote, _nextRoll;
    private bool _right;

    public AiDrummer(PlayableChart chart, TaikoCourse course, int seed, Func<(int Player, int Ai)> territory)
    {
        chart = chart.OnRoute(ChartBranch.Master);
        _notes = [.. chart.HitObjects];
        // Rolls and balloons as the bot drums them (only the presses inside a long note).
        _rolls = [.. GameplayAutoplay.InputsFor(chart, RollInterval)
            .Where(input => chart.LongNotes.Any(note => input.Time >= note.StartTime && input.Time <= note.EndTime))
            .Select(input => (input.Time, key(input.Action)))];
        _windows = TaikoJudgementWindows.ForCourse(chart.Key.Song.Source == SongSourceKind.OsuLazer ? TaikoCourse.Oni : course);
        _greatWeight = AiBattleRules.Weights(course).Great;
        _territory = territory;
        _random = new Random(seed);
    }

    public SdlKeyboardSnapshot Apply(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        while (_nextNote < _notes.Length && _notes[_nextNote].StartTime <= chartTime + Lead)
            decide(_notes[_nextNote++]);
        while (_nextRoll < _rolls.Length && _rolls[_nextRoll].Time <= chartTime + Lead)
            _pending.Add(_rolls[_nextRoll++]);
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
        // How many 良 AI-don is ahead by this section: the further ahead, the likelier a 可 or a miss.
        var (player, ai) = _territory();
        var ahead = (ai - player) / (double)_greatWeight;
        var great = Math.Clamp(0.85 - 0.12 * ahead, 0.30, 0.98);
        var roll = _random.NextDouble();
        TimeSpan offset;
        if (roll < great)
            offset = TimeSpan.FromMilliseconds((_random.NextDouble() - 0.5) * _windows.Great.TotalMilliseconds);
        else if (roll < great + (1 - great) * 0.75)
        {
            var span = _windows.Good - _windows.Great;
            offset = (_windows.Great + span * (0.2 + 0.6 * _random.NextDouble())) * (_random.Next(2) == 0 ? -1 : 1);
        }
        else
            return; // a miss: no hit
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
