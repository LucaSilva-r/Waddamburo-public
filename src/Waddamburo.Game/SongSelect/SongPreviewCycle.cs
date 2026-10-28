namespace Waddamburo.Game.SongSelect;

/// <summary>Oni and Ura share one preview button; its icon and rating always change together.</summary>
public sealed class SongPreviewCycle
{
    public const int HoldTicks = 150;
    public const int FadeTicks = 15;
    private string? _song;
    private int _age;
    public bool ShowsUra { get; private set; }
    public int? Level { get; private set; }
    public float Opacity { get; private set; } = 1;

    public void Advance(string song, int? oni, int? ura, bool playing = true)
    {
        if (_song != song || !playing) { _song = song; _age = 0; }
        else _age++;
        var both = oni is not null && ura is not null;
        ShowsUra = ura is not null && (oni is null || _age / HoldTicks % 2 == 1);
        Level = ShowsUra ? ura : oni;
        var phase = _age % HoldTicks;
        Opacity = !both || _age < FadeTicks ? 1 : phase < FadeTicks ? VerticalSongBrowser.Ease(phase / (float)FadeTicks)
            : phase > HoldTicks - FadeTicks ? VerticalSongBrowser.Ease((HoldTicks - phase) / (float)FadeTicks) : 1;
    }
}
