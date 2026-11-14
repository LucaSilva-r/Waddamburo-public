using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>
/// The game's Waiwai movies, told from <see cref="WaiwaiVoltage"/> (traced session11-waiwai): the voltage on
/// donbg_w_00 and the one gauge, the clear effect at the norm, and the section cut-ins: SetCutinSynchro for a
/// together section, SetLight + SetCutinSolo for a solo, each turned off when its section ends.
/// </summary>
public sealed class GameWaiwaiMovies
{
    private readonly WaiwaiVoltage _voltage;
    private readonly LumenPlayer _gauge;
    private readonly LumenPlayer _backdrop;
    private readonly LumenPlayer _cutIn;
    private readonly LumenPlayer _soloAbove;
    private readonly LumenPlayer _soloUnder;
    private readonly LumenPlayer _clearEffects;

    public GameWaiwaiMovies(WaiwaiVoltage voltage, IReadOnlyDictionary<string, LumenPlayer> movies)
    {
        _voltage = voltage;
        _gauge = movies["gage_w_normal"];
        _backdrop = movies["donbg_w_00"];
        _cutIn = movies["cut_in"];
        _soloAbove = movies["action_solo_above_lane"];
        _soloUnder = movies["action_solo_under_don"];
        _clearEffects = movies["clear_effects"];
        call(_backdrop, "SetWaiwaiVoltageLevelupNorma", number(WaiwaiVoltage.LevelUpNorma), number(WaiwaiVoltage.ClearNorma));
        call(_soloAbove, "SetDefaultPlay", LumenHostValue.FromBoolean(true));
        call(_soloUnder, "SetDefaultPlay", LumenHostValue.FromBoolean(true));
        voltage.VoltageChanged += () =>
        {
            call(_backdrop, "SetWaiwaiVoltage", number(voltage.Voltage));
            call(_gauge, "SetCurrentGauge", number(voltage.Segments));
        };
        voltage.NormaReached += () => call(_clearEffects, "Voltage_ReachNorm");
        voltage.SectionChanged += sectionChanged;
    }

    private void sectionChanged(int previous, int current)
    {
        int? before = previous >= 0 ? _voltage.Sections[previous].Soloist : null;
        int? after = current >= 0 ? _voltage.Sections[current].Soloist : null;
        // Traced order: the ending section's cut-in goes off; a solo light only goes off when the solo
        // run ends (a solo handing to the other player's solo just lights the other side), then the
        // new section's light and cut-in come on.
        if (previous >= 0)
        {
            if (before is { } soloist)
                call(_cutIn, "SetCutinSolo", number(soloist), LumenHostValue.FromBoolean(false));
            else
                call(_cutIn, "SetCutinSynchro", LumenHostValue.FromBoolean(false));
        }
        if (before is not null && after is null)
            light(before.Value, on: false);
        if (current < 0)
            return;
        if (after is { } player)
        {
            light(player, on: true);
            call(_cutIn, "SetCutinSolo", number(player), LumenHostValue.FromBoolean(true));
        }
        else
            call(_cutIn, "SetCutinSynchro", LumenHostValue.FromBoolean(true));
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
