using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Skins.Green.Art;

/// <summary>
/// The drum sounds' (音色) names and icons for Song Select's tone board (<see cref="ToneSurfaces"/>): the
/// game's nutdata: Green's own tone_name NUT (all 17 names; the older tone_name pack has the first six) and the
/// tone_icon pack (icon_neiro_001-005: the later tones have none).
/// </summary>
public sealed class ToneArt(NutData nutData)
{
    private const string GreenNames = "S11100-1/appendable/00/tone_name/tone_name.nut";

    // Icons start at tone 1 (the plain drum has none).
    public RenderTextureId? Resolve(LumenNativeSurfaceKey key) =>
        !ToneSurfaces.TryParse(key, out var icon, out var tone) ? null
        : icon ? tone is >= 1 and <= 5 ? nutData.Texture("tone_icon/nutdatapack.ndp", tone - 1) : null
        : tone < nutData.Textures(GreenNames).Length ? nutData.Texture(GreenNames, tone)
        : nutData.Texture("tone_name/nutdatapack.ndp", tone);
}
