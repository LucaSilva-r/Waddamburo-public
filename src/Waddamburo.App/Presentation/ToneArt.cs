using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The drum sounds' (音色) names and icons for Song Select's tone board (<see cref="ToneSurfaces"/>): the
/// game's nutdata/tone_name and tone_icon packs (tone_name_000-005, icon_neiro_001-005).
/// </summary>
internal sealed class ToneArt(NutData nutData)
{
    // Icons start at tone 1 (the plain drum has none).
    public RenderTextureId? Resolve(LumenNativeSurfaceKey key) =>
        !ToneSurfaces.TryParse(key, out var icon, out var tone) ? null
        : icon ? nutData.Texture("tone_icon/nutdatapack.ndp", tone - 1)
        : nutData.Texture("tone_name/nutdatapack.ndp", tone);
}
