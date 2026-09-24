using System.Collections.Immutable;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>What the Waiwai results show.</summary>
internal sealed record WaiwaiOutcome(int GaugeSegments, int DuetPercent, IReadOnlyList<bool> RareNotesHit);

/// <summary>
/// What both Waiwai players share (traced session11-waiwai): the voltage (0-10000) on donbg_w_00 and
/// the one gauge (voltage / 200 segments), the clear effect when the voltage reaches its norm (7000),
/// and the section cut-ins: SetCutinSynchro for a together section, SetLight + SetCutinSolo for a
/// solo, each turned off when its section ends.
/// </summary>
internal sealed class WaiwaiStage
{
    private const int MaxVoltage = 10000;
    private const int LevelUpNorma = 3000;
    private const int ClearNorma = 7000;
    // ponytail: measured on linda hard (hits 36/37 alternating, a miss -64); the real rates come
    // from waiwaiconfig.xml's voltage/support settings and the note count, not yet known.
    private const double HitVoltage = 36.5;
    private const double MissVoltage = -64;

    private readonly LumenPlayer _gauge;
    private readonly LumenPlayer _backdrop;
    private readonly LumenPlayer _cutIn;
    private readonly LumenPlayer _soloAbove;
    private readonly LumenPlayer _soloUnder;
    private readonly LumenPlayer _clearEffects;
    private readonly ImmutableArray<WaiwaiSectionTime> _sections;
    private readonly Action<GameplaySoundEvent> _sound;
    private double _voltage;
    private bool _reachedNorma;
    private int _section = -1; // index of the section being played, -1 outside

    public WaiwaiStage(IReadOnlyDictionary<string, LumenPlayer> movies, ImmutableArray<WaiwaiSectionTime> sections,
        Action<GameplaySoundEvent> sound)
    {
        _gauge = movies["gage_w_normal"];
        _backdrop = movies["donbg_w_00"];
        _cutIn = movies["cut_in"];
        _soloAbove = movies["action_solo_above_lane"];
        _soloUnder = movies["action_solo_under_don"];
        _clearEffects = movies["clear_effects"];
        _sections = sections;
        _sound = sound;
        call(_backdrop, "SetWaiwaiVoltageLevelupNorma", number(LevelUpNorma), number(ClearNorma));
        call(_soloAbove, "SetDefaultPlay", LumenHostValue.FromBoolean(true));
        call(_soloUnder, "SetDefaultPlay", LumenHostValue.FromBoolean(true));
        _sound(GameplaySoundEvent.WaiwaiStart);
    }

    public int Segments => (int)_voltage / 200;

    // Per synchro note (by time): lanes that judged it, lanes that hit it. Rare notes: hit by anyone.
    private readonly Dictionary<TimeSpan, (int Judged, int Hit)> _synchro = [];
    private readonly Dictionary<TimeSpan, bool> _rare = [];

    /// <summary>The results screen's numbers: gauge, duet %, rare notes and whether each was hit.</summary>
    public WaiwaiOutcome Outcome
    {
        get
        {
            var judged = _synchro.Values.Where(static note => note.Judged == 2).ToArray();
            // ponytail: duet % read as the synchro notes both players hit (traced 82 with P1 missing on
            // purpose, 96-100 in clean runs); the game's formula is unknown.
            var duet = judged.Length == 0 ? 100 : (int)Math.Round(100.0 * judged.Count(static note => note.Hit == 2) / judged.Length);
            return new(Segments, duet, [.. _rare.OrderBy(static pair => pair.Key).Select(static pair => pair.Value)]);
        }
    }

    public TaikoGaugeState State => _voltage >= MaxVoltage ? TaikoGaugeState.Full
        : _voltage >= ClearNorma ? TaikoGaugeState.Cleared
        : TaikoGaugeState.BelowClear;

    /// <summary>The shared gauge band changed: both players' Dons follow it (one merged gauge).</summary>
    public event Action<TaikoGaugeState>? StateChanged;

    /// <summary>A judgement from either lane.</summary>
    public void Apply(TaikoNoteJudgement judgement)
    {
        if (judgement.StrongHitCompleted || judgement.Result is not { } result)
            return;
        var hit = result != TaikoHitResult.Miss;
        var time = judgement.HitObject.StartTime;
        if (judgement.HitObject.IsSynchro)
            _synchro[time] = _synchro.TryGetValue(time, out var note) ? (note.Judged + 1, note.Hit + (hit ? 1 : 0)) : (1, hit ? 1 : 0);
        if (judgement.HitObject.IsRare)
            _rare[time] = _rare.GetValueOrDefault(time) || hit;
        var state = State;
        _voltage = Math.Clamp(_voltage + (result == TaikoHitResult.Miss ? MissVoltage : HitVoltage), 0, MaxVoltage);
        call(_backdrop, "SetWaiwaiVoltage", number((int)_voltage));
        call(_gauge, "SetCurrentGauge", number(Segments));
        if (!_reachedNorma && _voltage >= ClearNorma)
        {
            _reachedNorma = true;
            call(_clearEffects, "Voltage_ReachNorm");
        }
        if (State != state)
            StateChanged?.Invoke(State);
    }

    public void Update(TimeSpan time)
    {
        var section = -1;
        for (var index = 0; index < _sections.Length; index++)
            if (time >= _sections[index].Start && time < _sections[index].End)
                section = index;
        if (section == _section)
            return;
        int? before = _section >= 0 ? _sections[_section].Soloist : null;
        int? after = section >= 0 ? _sections[section].Soloist : null;
        var wasSolo = _section >= 0 && before is not null;
        var isSolo = section >= 0 && after is not null;
        // Traced order: the ending section's cut-in goes off; a solo light only goes off when the solo
        // run ends (a solo handing to the other player's solo just lights the other side), then the
        // new section's light and cut-in come on.
        if (_section >= 0)
        {
            if (before is { } previous)
                call(_cutIn, "SetCutinSolo", number(previous), LumenHostValue.FromBoolean(false));
            else
                call(_cutIn, "SetCutinSynchro", LumenHostValue.FromBoolean(false));
        }
        if (wasSolo && !isSolo)
            light(before!.Value, on: false);
        _section = section;
        if (section < 0)
            return;
        if (after is { } player)
        {
            light(player, on: true);
            call(_cutIn, "SetCutinSolo", number(player), LumenHostValue.FromBoolean(true));
            _sound(GameplaySoundEvent.SoloCutIn);
        }
        else
        {
            call(_cutIn, "SetCutinSynchro", LumenHostValue.FromBoolean(true));
            _sound(GameplaySoundEvent.SynchroCutIn);
        }
    }

    private void light(int player, bool on)
    {
        call(_soloAbove, "SetLight", number(player), LumenHostValue.FromBoolean(on));
        call(_soloUnder, "SetLight", number(player), LumenHostValue.FromBoolean(on));
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    // The game also calls callbacks some movies lack (donbg_w_00's SetHit); a missing one is reported, not fatal.
    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            Console.Error.WriteLine($"Waiwai movie lacks callback '{name}'.");
    }
}
