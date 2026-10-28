namespace Waddamburo.Game.SongSelect;

/// <summary>Screen-space offsets; the native artwork keeps its authored size.</summary>
public static class DifficultySelectLayout
{
    public const float ChartX = 16, ChartY = 48;
    public const float SettingsX = -28, SettingsY = 62;
    public const float RightEdge = 1016;
    // Twelve more pixels above the crowns; the bottom edge stays fixed.
    public const float PanelTopInset = 42;
    // The edited CourseSelect group uses 0.82 of the original column size.
    public const float PlayerMarkerScale = .82f;
    public const float TitleLeft = 352, TitleRight = 968, TitleY = 16;
    public const int TitleHeight = 96, TitleLineHeight = 64;
}
