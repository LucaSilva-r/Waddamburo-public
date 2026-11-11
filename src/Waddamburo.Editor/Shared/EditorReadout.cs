namespace Waddamburo.Editor;

/// <summary>Playing numbers refresh at ten Hz; paused/seeking values are precise without changing column widths.</summary>
public sealed class EditorReadout
{
    private long _next;
    public TimeSpan Position { get; private set; }
    public double Beat { get; private set; }
    public void Update(long milliseconds, TimeSpan position, double beat, bool paused)
    {
        if (!paused && milliseconds < _next && Math.Abs((position - Position).TotalSeconds) < 1) return;
        Position = position; Beat = beat; _next = milliseconds + 100;
    }
}
