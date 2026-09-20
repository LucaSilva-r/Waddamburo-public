using System.Collections.Immutable;

namespace Waddamburo.Lumen.Runtime;

/// <summary>Immutable Flash key-code state for one Lumen simulation tick.</summary>
public sealed class LumenInputSnapshot
{
    private readonly ImmutableHashSet<int> _pressedKeyCodes;

    public static LumenInputSnapshot Empty { get; } = new([]);

    public LumenInputSnapshot(IEnumerable<int> pressedKeyCodes)
    {
        ArgumentNullException.ThrowIfNull(pressedKeyCodes);
        _pressedKeyCodes = pressedKeyCodes.ToImmutableHashSet();
    }

    public bool IsDown(int keyCode) => _pressedKeyCodes.Contains(keyCode);
}
