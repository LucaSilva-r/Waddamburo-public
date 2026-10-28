using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>Retains rapid rim hits while the native difficulty cursor finishes a move.</summary>
public sealed class DifficultySelectScroll(Func<int, double?> selectedIndex)
{
    private static readonly int[] Left = ['A', 'D'], Right = ['S', 'F'];
    private readonly int[] _pending = new int[2], _sent = new int[2], _wait = new int[2];
    private readonly double[] _before = new double[2];

    public LumenInputSnapshot Before(LumenInputSnapshot input, Func<int, bool> chartFocused)
    {
        var keys = input.PressedKeyCodes;
        var confirm = keys.Contains('Z') || keys.Contains('C') || keys.Contains(13) || keys.Contains(27);
        for (var side = 0; side < 2; side++)
        {
            _sent[side] = 0;
            var current = selectedIndex(side);
            if (confirm || !chartFocused(side) || current is null)
            {
                _pending[side] = _wait[side] = 0;
                continue;
            }
            _before[side] = current.Value;
            // The right edge also receives the original secret-chart unlock hits.
            // Pass these through once; an unchanged index is not a dropped move here.
            if (current >= 3 && _pending[side] >= 0 && !keys.Contains(Left[side]))
            {
                _pending[side] = _wait[side] = 0;
                continue;
            }
            var direction = (keys.Contains(Right[side]) ? 1 : 0) - (keys.Contains(Left[side]) ? 1 : 0);
            if (direction != 0)
            {
                // A reversal cancels stale movement immediately.
                if (Math.Sign(_pending[side]) != direction) _pending[side] = 0;
                _pending[side] = Math.Clamp(_pending[side] + direction, -8, 8);
                _wait[side] = 0;
            }
            keys = keys.Remove(Left[side]).Remove(Right[side]);
            if (_pending[side] == 0) continue;
            if (++_wait[side] > 30) { _pending[side] = 0; continue; }
            _sent[side] = Math.Sign(_pending[side]);
            keys = keys.Add(_sent[side] > 0 ? Right[side] : Left[side]);
        }
        return new(keys);
    }

    public void After()
    {
        for (var side = 0; side < 2; side++)
            if (_sent[side] != 0 && selectedIndex(side) is { } after && after != _before[side])
            {
                _pending[side] -= _sent[side];
                _wait[side] = 0;
            }
    }

}
