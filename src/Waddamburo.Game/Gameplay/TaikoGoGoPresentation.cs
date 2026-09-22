using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>Sends movie transitions only when the chart's Go-Go state changes.</summary>
public sealed class TaikoGoGoPresentation(ImmutableArray<ChartEffectPoint> points, Action<bool> setGoGo)
{
    private bool? _active;

    public void Update(TimeSpan time)
    {
        var active = false;
        foreach (var point in points)
        {
            if (point.Time > time) break;
            active = point.IsGoGo;
        }
        if (_active == active) return;
        setGoGo(active);
        _active = active;
    }
}
