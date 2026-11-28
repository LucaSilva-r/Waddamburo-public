using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The game's movies as they reach the GPU: read from the user's archives, their BC1/BC3 textures kept compressed when
/// the GPU draws them, cached upscales swapped in (when installed and on), and patched in memory as they decode (the
/// files stay untouched) by the skin.
/// </summary>
internal static class GameMovieContent
{
    /// <summary>The content source for <paramref name="lumenRoot"/>, and the upscaler when its model is installed.</summary>
    /// <param name="application">The renderer (which block formats it draws; BC7 for upscales).</param>
    /// <param name="skin">The skin whose movies these are (it patches them as they decode).</param>
    /// <param name="lumenRoot">The lumendata/packed folder.</param>
    /// <param name="cacheRoot">The cache folder (upscales go in its "upscaled"); null: the user cache folder.</param>
    /// <param name="settings">The settings, read live (upscaling on or off).</param>
    public static (DirectoryLumenMovieContentSource Source, UpscaleTool? Upscale) Create(SdlApplication application, ISkin skin,
        string lumenRoot, string? cacheRoot, Func<ArcadeSettings> settings)
    {
        LumenTextureContent.Bc7Decoder = static (blocks, width, height) => Bc7.Decode(blocks.Span, width, height);
        var upscale = UpscaleTool.Find(cacheRoot is null ? null : Path.Combine(cacheRoot, "upscaled"));
        var source = new DirectoryLumenMovieContentSource(lumenRoot)
        {
            // Cached upscales replace textures as movies decode (opt-in): the GPU only ever gets those.
            Decoded = (archive, content) => patch(skin, archive, content,
                upscale is not null && settings().UpscaleTextures && application.ShowUpscaled ? (upscale, application.SupportsBc7) : null),
            // The game's BC1/BC3 textures go to the GPU as they are: a quarter of the memory, no decoding.
            // WADDAMBURO_DECODE_TEXTURES=1 decodes them all to RGBA as before (comparisons).
            KeepCompressed = DiagnosticSwitches.DecodeTextures != "1"
                && application.Supports(TextureBlockFormat.Bc1) && application.Supports(TextureBlockFormat.Bc3),
        };
        if (upscale is not null)
        {
            upscale.LumenRoot = lumenRoot;
            source.Loaded = upscale.RecordUsed;
            SceneTextures.Upscaler = new TextureUpscaler(upscale, settings);
            skin.Upscaled = texture => settings().UpscaleTextures && application.ShowUpscaled
                ? upscale.ApplyCached(texture, null, application.SupportsBc7) : texture;
            FolderArtPainter.Upscaled = (width, height, rgba) =>
                settings().UpscaleTextures && application.ShowUpscaled
                && upscale.Load(UpscaleTool.Key(new UpscaleSource(width, height, rgba))) is { } cached
                    ? (cached.Width, cached.Height, Bc7.Decode(cached.Bc7.Span, (int)cached.Width, (int)cached.Height))
                    : null;
            Console.WriteLine($"Texture upscaling {(settings().UpscaleTextures ? "on" : "off")}, "
                + $"{settings().UpscaleThreads} background thread(s), cache {upscale.Cache}.");
        }
        return (source, upscale);
    }

    /// <summary>
    /// A movie package's texture as straight RGBA: a PNG, or an SVG drawn at its own size
    /// (see <see cref="LumenMovieContent.LoadPackage"/>).
    /// </summary>
    /// <summary>
    /// A package texture for the size a shape shows it at (pixels): an SVG drawn at that size, a PNG as it is.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba)? DecodeSized(byte[] file, int width, int height) =>
        file is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? DecodeImage(file) : SvgTextures.DecodeAt(file, width, height);

    /// <summary>
    /// Pixels a movie unit takes on screen (the window's height over 720), kept by the frame loop: packages loaded from
    /// then on draw their SVGs for it.
    /// </summary>
    public static float PixelsPerUnit { get; set; } = 2;

    public static (int Width, int Height, byte[] Rgba)? DecodeImage(byte[] file) =>
        file is [0x89, (byte)'P', (byte)'N', (byte)'G', ..]
            ? Waddamburo.Platform.Sdl.Text.SdlPng.Decode(file) is { } image ? ((int)image.Width, (int)image.Height, image.Pixels) : null
            : SvgTextures.Decode(file);

    // The skin patches its movies; the cached upscales go in where it says.
    private static void patch(ISkin skin, string archive, LumenMovieContent content, (UpscaleTool Tool, bool Bc7)? upscale) =>
        skin.Decoded(archive, content, () => upscale?.Tool.ApplyCached(archive, content, upscale.Value.Bc7),
            pixels => upscale?.Tool.MarkUpscaled(pixels));
}
