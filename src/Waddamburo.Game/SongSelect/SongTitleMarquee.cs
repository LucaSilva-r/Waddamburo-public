namespace Waddamburo.Game.SongSelect;

/// <summary>A long title pauses at both ends and slides at a readable, fixed speed inside its Lumen mask.</summary>
public static class SongTitleMarquee
{
    public static float Offset(int age, float extent, float viewport = 400)
    {
        if (extent <= viewport || age <= 90) return 0;
        var travel = Math.Max(1, (int)Math.Ceiling(extent - viewport)); // one logical pixel per 60-Hz tick
        var phase = (age - 90) % (travel * 2 + 150);
        if (phase < travel) return phase;
        if (phase < travel + 60) return extent - viewport;
        if (phase < travel * 2 + 60) return Math.Max(0, extent - viewport - (phase - travel - 60));
        return 0;
    }
}
