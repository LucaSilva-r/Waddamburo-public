using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Green's feature folder art (Song Select's server-chosen 特集 and collab folders): appendable/00/featureboard_{v,h,icon,image}
/// in Green's nutdata, one texture per folder art (東方Project, アイドルマスター, #コンパス, ドコドン...). The event folder
/// names a part by <see cref="Waddamburo.Game.SongSelect.SongSelectEvent.FeatureBoard"/>.
/// </summary>
internal sealed class FeatureBoardArt(NutData nutData)
{
    private const string Prefix = "featureboard:";

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith(Prefix, StringComparison.Ordinal) || key.Value[Prefix.Length..].Split(':') is not [var part, var index]
            || part is not ("v" or "h" or "icon" or "image") || !int.TryParse(index, out var art))
            return null;
        // ponytail: Green's cabinet folder, as the other nutdata art.
        return nutData.Texture($"S11100-1/appendable/00/featureboard_{part}/featureboard_{part}.nut", art);
    }
}
