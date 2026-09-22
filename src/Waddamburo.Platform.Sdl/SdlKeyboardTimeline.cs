using System.Collections.Immutable;

namespace Waddamburo.Platform.Sdl;

/// <summary>Immutable exact-tick keyboard pulses for deterministic input probes.</summary>
public sealed class SdlKeyboardTimeline
{
    private readonly ImmutableDictionary<int, ImmutableArray<SdlKeyboardKey>> _keysByTick;

    public static SdlKeyboardTimeline Empty { get; } = new([]);

    public SdlKeyboardTimeline(IEnumerable<(int Tick, SdlKeyboardKey Key)> pulses)
    {
        ArgumentNullException.ThrowIfNull(pulses);
        var builder = ImmutableDictionary.CreateBuilder<int, ImmutableArray<SdlKeyboardKey>>();
        foreach (var group in pulses.GroupBy(pulse => pulse.Tick))
        {
            if (group.Key <= 0)
                throw new ArgumentOutOfRangeException(nameof(pulses), "Input pulse ticks must be positive.");
            builder.Add(group.Key, [.. group.Select(pulse => pulse.Key).Distinct().Order()]);
        }
        _keysByTick = builder.ToImmutable();
    }

    public bool IsEmpty => _keysByTick.IsEmpty;

    public SdlKeyboardSnapshot Apply(int tick, SdlKeyboardSnapshot liveKeyboard)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tick);
        ArgumentNullException.ThrowIfNull(liveKeyboard);
        return !_keysByTick.TryGetValue(tick, out var scripted)
            ? liveKeyboard
            : new SdlKeyboardSnapshot(liveKeyboard.PressedKeys.Concat(scripted),
                liveKeyboard.Presses.Concat(scripted.Select(key => new SdlKeyPress(key, liveKeyboard.Timestamp))),
                liveKeyboard.Timestamp);
    }
}
