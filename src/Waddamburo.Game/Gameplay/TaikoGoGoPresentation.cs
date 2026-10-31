namespace Waddamburo.Game.Gameplay;

/// <summary>Sends movie transitions only when the chart's Go-Go state changes.</summary>
public sealed class TaikoGoGoPresentation(Func<TimeSpan, bool> isGoGo, Action<bool> setGoGo)
{
    private bool? _active;

    public void Update(TimeSpan time)
    {
        var active = isGoGo(time);
        if (_active == active) return;
        setGoGo(active);
        _active = active;
    }
}
