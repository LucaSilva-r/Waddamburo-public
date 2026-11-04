using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The boot's music credit pages (kidou's copyright1-3): nutdata/S11100-1/copyright/copyright.nut, one 1280x720
/// texture per page (Green has two). The movie shows as many pages as _global.copyright_num says, each page a
/// native fill named after its label.
/// </summary>
internal sealed class CopyrightArt(SdlApplication application, string dataRoot)
{
    // ponytail: Green's cabinet folder, as the other nutdata art; other releases' folders when they're supported.
    private readonly string _file = Path.Combine(dataRoot, "nutdata", "S11100-1", "copyright", "copyright.nut");
    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId?> _uploaded = [];
    private IReadOnlyList<NutTexture>? _pages;

    /// <summary>The page's surface, <paramref name="page"/> counting from 1.</summary>
    public static LumenNativeSurfaceKey Key(int page) => new($"copyright:{page}");

    /// <summary>The pages there are (the movie has room for three).</summary>
    public int Pages => Math.Min(pages().Count, 3);

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!key.Value.StartsWith("copyright:", StringComparison.Ordinal))
            return null;
        if (_uploaded.TryGetValue(key, out var texture))
            return texture;
        RenderTextureId? uploaded = null;
        if (int.TryParse(key.Value["copyright:".Length..], out var page) && page >= 1 && page <= pages().Count)
        {
            var art = pages()[page - 1];
            uploaded = application.UploadRgba8((uint)art.Width, (uint)art.Height, NutTextureDecoder.DecodeRgba8(art));
        }
        return _uploaded[key] = uploaded;
    }

    private IReadOnlyList<NutTexture> pages()
    {
        if (_pages is not null)
            return _pages;
        try
        {
            _pages = File.Exists(_file) ? NutFile.Parse(File.ReadAllBytes(_file)).Textures : [];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning COPYRIGHT_ART: {exception.Message}");
            _pages = [];
        }
        return _pages;
    }
}
