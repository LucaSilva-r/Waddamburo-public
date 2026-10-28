namespace Waddamburo.Game.SongSelect;

/// <summary>Intrinsic song-list geometry: one growing category, translated as a whole.</summary>
public static class VerticalSongRowsLayout
{
    public const float RowPitch = 100;
    public const float ScreenPitch = RowPitch * .65f;

    public static float RowPosition(float board) => 150 + 30f / .65f + (board - 2) * RowPitch;
    public static float CategoryHeight(int count) => 24 + Math.Max(1, count) * ScreenPitch;
    public static float ScrollOffset(float cursor) => Math.Max(0, cursor - 2) * ScreenPitch;
    public static float RevealHeight(int count, float progress)
    {
        var height = CategoryHeight(count);
        return progress >= 1 ? height : 270 + (Math.Min(height, 600) - 270) * Math.Clamp(progress, 0, 1);
    }
}

/// <summary>A single continuous cursor shared by every recycled board, including rapid reversals.</summary>
public sealed class VerticalSongRowsMotion
{
    public float Cursor { get; private set; }
    public float Target { get; private set; }
    public bool Initialized { get; private set; }
    public float SelectionAlpha => Math.Clamp(1 - Math.Abs(Target - Cursor) * 3, 0, 1);

    public void Advance(int selected, int count, bool reset = false)
    {
        selected = Math.Clamp(selected, 0, Math.Max(0, count - 1));
        if (!Initialized || reset || Math.Abs(selected - Target) > 5)
        {
            Cursor = Target = selected;
            Initialized = true;
            return;
        }
        Target = selected;
        Cursor = approach(Cursor, Target);
    }

    private static float approach(float value, float target) =>
        Math.Abs(value - target) < .002f ? target : value + (target - value) * .3f;
}
