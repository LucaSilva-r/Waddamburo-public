using System.Collections.Immutable;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>The presses a tick acts on: a drum hit's side, Space (skip) and F1 (back to the attract loop).</summary>
internal readonly record struct TickPresses(int? DrumSide, bool Skip, bool Attract);

/// <summary>
/// Live presses reach only the per-frame callback; ticks see held keys. Presses between two ticks are
/// latched here so the next tick still sees them (scripted --press pulses arrive in the tick instead),
/// and held keys become one-tick pulses for the movies.
/// </summary>
internal sealed class InputLatches
{
    private int? _drumSide;
    private bool _skip;
    private bool _attract;
    private int _coins;
    private readonly HashSet<SdlKeyboardKey> _presses = [];
    private ImmutableHashSet<SdlKeyboardKey> _heldLastTick = [];
    private bool _escapeWasDown;

    /// <summary>Latches one rendered frame's presses.</summary>
    public void Frame(SdlKeyboardSnapshot keyboard)
    {
        _drumSide ??= drumSide(keyboard.Presses);
        _skip |= keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
        _attract |= keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.F1);
        _presses.UnionWith(keyboard.Presses.Select(static press => press.Key));
        _coins += keyboard.Presses.Count(static press => press.Key == SdlKeyboardKey.F2);
    }

    /// <summary>The tick's presses (latched and its own), cleared for the next tick.</summary>
    public TickPresses Take(SdlKeyboardSnapshot keys)
    {
        var presses = new TickPresses(
            _drumSide ?? drumSide(keys.Presses),
            _skip || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.Space),
            _attract || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.F1));
        _drumSide = null;
        _skip = _attract = false;
        return presses;
    }

    /// <summary>Coins inserted (F2) since the last call.</summary>
    public int TakeCoins(SdlKeyboardSnapshot keys)
    {
        var coins = _coins + keys.Presses.Count(static press => press.Key == SdlKeyboardKey.F2);
        _coins = 0;
        return coins;
    }

    /// <summary>
    /// Movies see a key for one tick per press, as a drum hit is a pulse: a held key would read as a
    /// new hit in every panel that starts polling while it is still down (the entry's card dialog
    /// closing under a decide hit joined P1 as well). Taps between ticks count too.
    /// </summary>
    public SdlKeyboardSnapshot Pulses(SdlKeyboardSnapshot keys)
    {
        var held = keys.PressedKeys.ToImmutableHashSet();
        var pulses = held.Except(_heldLastTick).Union(_presses).Union(keys.Presses.Select(static press => press.Key));
        _heldLastTick = held;
        _presses.Clear();
        return new SdlKeyboardSnapshot(pulses, keys.Presses, keys.Timestamp);
    }

    /// <summary>Escape went down this tick (in the scene's mapped keys).</summary>
    public bool EscapePressed(SdlKeyboardSnapshot keys)
    {
        var down = keys.IsDown(SdlKeyboardKey.Escape);
        var pressed = down && !_escapeWasDown;
        _escapeWasDown = down;
        return pressed;
    }

    private static int? drumSide(IEnumerable<SdlKeyPress> presses) => presses.Select(static press => press.Key switch
    {
        SdlKeyboardKey.D or SdlKeyboardKey.F or SdlKeyboardKey.J or SdlKeyboardKey.K => 0,
        SdlKeyboardKey.Z or SdlKeyboardKey.X or SdlKeyboardKey.C or SdlKeyboardKey.V => 1,
        _ => (int?)null,
    }).FirstOrDefault(static side => side is not null);
}
