namespace Waddamburo.Game.SongSelect;

/// <summary>Reveals the opened song title before its difficulty buttons.</summary>
public sealed class SongPreviewReveal
{
    public const int TitleTicks = 8, GapTicks = 4, DifficultyTicks = 8;
    private (int Genre, int Board) _selection = (-1, -1);
    private int _age, _previousAge;

    public void Advance(int genre, int board, bool expanded, bool ready)
    {
        if (_selection != (genre, board) || !expanded || !ready)
            _age = _previousAge = 0;
        _selection = (genre, board);
        _previousAge = _age;
        if (expanded && ready) _age = Math.Min(TitleTicks + GapTicks + DifficultyTicks, _age + 1);
    }

    /// <summary>Freeze the visible phase while the enclosing card fades out.</summary>
    public void Hold() => _previousAge = _age;

    private float age(float interpolation) => _previousAge + (_age - _previousAge) * Math.Clamp(interpolation, 0, 1);
    public float TitleOpacity(float interpolation) => VerticalSongBrowser.Ease(age(interpolation) / TitleTicks);
    public float DifficultyOpacity(float interpolation) =>
        VerticalSongBrowser.Ease((age(interpolation) - TitleTicks - GapTicks) / DifficultyTicks);
}
