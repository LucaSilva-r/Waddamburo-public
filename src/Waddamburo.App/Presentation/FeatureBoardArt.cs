using Waddamburo.Skins.Green.Art;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The game's feature folder art (Song Select's server-chosen 特集 and collab folders): each release folder of Green's nutdata
/// (White ST7100-1 to Green S11100-1) keeps its own appendable/00/featureboard_{v,h,icon,image}, one texture per folder art
/// (東方Project, パズドラ, A3!...). The event folder names a part by <see cref="Waddamburo.Game.SongSelect.SongSelectEvent.FeatureBoard"/>.
/// </summary>
internal sealed class FeatureBoardArt(NutData nutData)
{
    private const string Prefix = "featureboard:";

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        // The release names a folder: letters, digits and '-' only (it comes from the server).
        if (!key.Value.StartsWith(Prefix, StringComparison.Ordinal) || key.Value[Prefix.Length..].Split(':') is not [var part, var release, var index]
            || part is not ("v" or "h" or "icon" or "image") || release.Length is 0 or > 16
            || !release.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-') || !int.TryParse(index, out var art))
            return null;
        return nutData.Texture(path(release, part), art);
    }

    /// <summary>
    /// Whether this Green has all four parts of an art ("release:index"): a dump imported with Green's own files only, or an
    /// older one, can lack another release's featureboard set. Read from disk, nothing uploaded.
    /// </summary>
    public bool Has(string art) =>
        art.Split(':') is [var release, var index] && int.TryParse(index, out var at) && at >= 0
        && release.Length is > 0 and <= 16 && release.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && Parts.All(part => at < nutData.Textures(path(release, part)).Length);

    private static readonly string[] Parts = ["v", "h", "icon", "image"];

    private static string path(string release, string part) => $"{release}/appendable/00/featureboard_{part}/featureboard_{part}.nut";
}
