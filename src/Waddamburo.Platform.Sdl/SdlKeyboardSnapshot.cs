using System.Collections.Immutable;

namespace Waddamburo.Platform.Sdl;

/// <summary>Portable keyboard state captured after SDL event processing.</summary>
public sealed class SdlKeyboardSnapshot
{
    private readonly ImmutableHashSet<SdlKeyboardKey> _pressedKeys;

    public static SdlKeyboardSnapshot Empty { get; } = new([]);

    public SdlKeyboardSnapshot(IEnumerable<SdlKeyboardKey> pressedKeys,
        IEnumerable<SdlKeyPress>? presses = null, TimeSpan timestamp = default)
    {
        ArgumentNullException.ThrowIfNull(pressedKeys);
        _pressedKeys = pressedKeys.ToImmutableHashSet();
        Presses = presses is null ? [] : [.. presses.OrderBy(press => press.Timestamp)];
        Timestamp = timestamp;
    }

    public TimeSpan Timestamp { get; }
    public ImmutableArray<SdlKeyPress> Presses { get; }

    public bool IsDown(SdlKeyboardKey key) => _pressedKeys.Contains(key);

    public ImmutableArray<SdlKeyboardKey> PressedKeys => [.. _pressedKeys.Order()];
}

public readonly record struct SdlKeyPress(SdlKeyboardKey Key, TimeSpan Timestamp);

public enum SdlKeyboardKey
{
    Backspace = 8,
    Enter = 13,
    Escape = 27,
    Space = 32,
    Left = 37,
    Up = 38,
    Right = 39,
    Down = 40,
    Digit0 = 48,
    Digit1 = 49,
    Digit2 = 50,
    Digit3 = 51,
    Digit4 = 52,
    Digit5 = 53,
    Digit6 = 54,
    Digit7 = 55,
    Digit8 = 56,
    Digit9 = 57,
    A = 65,
    B = 66,
    C = 67,
    D = 68,
    E = 69,
    F = 70,
    G = 71,
    H = 72,
    I = 73,
    J = 74,
    K = 75,
    L = 76,
    M = 77,
    N = 78,
    O = 79,
    P = 80,
    Q = 81,
    R = 82,
    S = 83,
    T = 84,
    U = 85,
    V = 86,
    W = 87,
    X = 88,
    Y = 89,
    Z = 90,
    F1 = 112,
    F2 = 113,
}
