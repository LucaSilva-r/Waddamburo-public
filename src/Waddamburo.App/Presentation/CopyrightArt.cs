using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The boot's music credit pages (kidou's copyright1-3): nutdata/S11100-1/copyright/copyright.nut, one 1280x720
/// texture per page (Green has two). The movie shows as many pages as _global.copyright_num says, each page a
/// native fill named after its label.
/// </summary>
internal sealed class CopyrightArt(NutData nutData)
{
    // ponytail: Green's cabinet folder, as the other nutdata art; other releases' folders when they're supported.
    private const string File = "S11100-1/copyright/copyright.nut";

    /// <summary>The page's surface, <paramref name="page"/> counting from 1.</summary>
    public static LumenNativeSurfaceKey Key(int page) => new($"copyright:{page}");

    /// <summary>The pages there are (the movie has room for three).</summary>
    public int Pages => Math.Min(nutData.Textures(File).Length, 3);

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key) =>
        key.Value.StartsWith("copyright:", StringComparison.Ordinal) && int.TryParse(key.Value["copyright:".Length..], out var page)
            ? nutData.Texture(File, page - 1) : null;
}
