using System.Collections.Immutable;

namespace Waddamburo.Game.Gameplay;

/// <summary>What the Waiwai results show.</summary>
public sealed record WaiwaiOutcome(int GaugeSegments, int DuetPercent, IReadOnlyList<bool> RareNotesHit);

/// <summary>
/// What both Waiwai players share (traced session11-waiwai): the voltage (0-10000) and the one gauge (voltage / 200
/// segments), the norm (7000) that clears it, and which section is being played (together, or one player's solo).
/// The engine's rules only: the game's movies are told by the skin's adapter, from the events.
/// </summary>
public sealed class WaiwaiVoltage(ImmutableArray<WaiwaiSectionTime> sections)
{
    public const int MaxVoltage = 10000;
    public const int LevelUpNorma = 3000;
    public const int ClearNorma = 7000;
    // ponytail: measured on linda hard (hits 36/37 alternating, a miss -64); the real rates come
    // from waiwaiconfig.xml's voltage/support settings and the note count, not yet known.
    private const double HitVoltage = 36.5;
    private const double MissVoltage = -64;

    private double _voltage;
    private bool _reachedNorma;

    public ImmutableArray<WaiwaiSectionTime> Sections { get; } = sections;

    /// <summary>The section being played, -1 outside any.</summary>
    public int Section { get; private set; } = -1;

    public int Voltage => (int)_voltage;

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

    /// <summary>The voltage moved (after each judgement).</summary>
    public event Action? VoltageChanged;

    /// <summary>The voltage reached the norm, once.</summary>
    public event Action? NormaReached;

    /// <summary>The shared gauge band changed: both players' Dons follow it (one merged gauge).</summary>
    public event Action<TaikoGaugeState>? StateChanged;

    /// <summary>The section changed: the one before and the one now (-1 outside any).</summary>
    public event Action<int, int>? SectionChanged;

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
        VoltageChanged?.Invoke();
        if (!_reachedNorma && _voltage >= ClearNorma)
        {
            _reachedNorma = true;
            NormaReached?.Invoke();
        }
        if (State != state)
            StateChanged?.Invoke(State);
    }

    public void Update(TimeSpan time)
    {
        var section = -1;
        for (var index = 0; index < Sections.Length; index++)
            if (time >= Sections[index].Start && time < Sections[index].End)
                section = index;
        if (section == Section)
            return;
        var before = Section;
        Section = section;
        SectionChanged?.Invoke(before, section);
    }
}
