namespace Waddamburo.Game.SongSelect;

/// <summary>Text arrives near the end of expansion; the illustration grows afterwards.</summary>
public sealed class CategoryPreviewReveal
{
    public const int ArtworkTicks = 10;
    private float _expansion, _previousExpansion;
    private int _artworkAge, _previousArtworkAge;

    public void Advance(float expansion)
    {
        _previousExpansion = _expansion;
        _expansion = Math.Clamp(expansion, 0, 1);
        _previousArtworkAge = _artworkAge;
        if (_expansion <= 0) _artworkAge = _previousArtworkAge = 0;
        else if (_expansion >= 1) _artworkAge = Math.Min(ArtworkTicks, _artworkAge + 1);
        else if (_expansion > _previousExpansion) _artworkAge = _previousArtworkAge = 0;
        else _previousArtworkAge = _artworkAge; // Preserve the outgoing figure while it fades.
    }

    public float TextOpacity(float interpolation) => VerticalSongBrowser.Ease(
        (_previousExpansion + (_expansion - _previousExpansion) * Math.Clamp(interpolation, 0, 1) - .8f) / .2f);
    public float ArtworkScale(float interpolation) => VerticalSongBrowser.Ease(
        (_previousArtworkAge + (_artworkAge - _previousArtworkAge) * Math.Clamp(interpolation, 0, 1)) / ArtworkTicks);
}
