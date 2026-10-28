namespace Waddamburo.Game.SongSelect;

/// <summary>The circular edge of the old-style music wheel. Boards retain their size and orientation.</summary>
public static class SongSelectCircularPath
{
    public const float Radius = 480;

    /// <summary>Screen-space D arc: focused card at zero, other cards recede right without scaling.</summary>
    public static float VerticalOffset(float rowCenter, float focus = 288) =>
        1.1f * Inset(150 + (rowCenter - focus) / .65f);

    public static float PerspectiveScale(float rowCenter, float focus = 288) =>
        Math.Clamp(1 - Math.Max(0, Math.Abs(rowCenter - focus) - 2) * .0003f, .84f, 1);

    public static float Inset(float authoredX)
    {
        var vertical = Math.Min(Math.Abs((authoredX - 150) * .65f), Radius);
        return (Radius - MathF.Sqrt(Math.Max(0, Radius * Radius - vertical * vertical))) / 1.1f;
    }
}
