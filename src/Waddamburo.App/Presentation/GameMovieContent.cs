using System.Diagnostics;
using System.Runtime.InteropServices;
using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Patching;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The game's movies as they reach the GPU: read from the user's archives, their BC1/BC3 textures kept compressed when
/// the GPU draws them, cached upscales swapped in (when installed and on), and patched in memory as they decode (the
/// files stay untouched): pixel art enlarged by whole pixels, Song Select's genre art recoloured.
/// </summary>
internal static class GameMovieContent
{
    /// <summary>The content source for <paramref name="lumenRoot"/>, and the upscaler when its model is installed.</summary>
    /// <param name="application">The renderer (which block formats it draws; BC7 for upscales).</param>
    /// <param name="lumenRoot">The lumendata/packed folder.</param>
    /// <param name="cacheRoot">The cache folder (upscales go in its "upscaled"); null: the user cache folder.</param>
    /// <param name="settings">The settings, read live (upscaling on or off).</param>
    public static (DirectoryLumenMovieContentSource Source, UpscaleTool? Upscale) Create(SdlApplication application,
        string lumenRoot, string? cacheRoot, Func<ArcadeSettings> settings)
    {
        LumenTextureContent.Bc7Decoder = static (blocks, width, height) => Bc7.Decode(blocks.Span, width, height);
        var upscale = UpscaleTool.Find(cacheRoot is null ? null : Path.Combine(cacheRoot, "upscaled"));
        var source = new DirectoryLumenMovieContentSource(lumenRoot)
        {
            // Cached upscales replace textures as movies decode (opt-in): the GPU only ever gets those.
            Decoded = (archive, content) => patch(archive, content,
                upscale is not null && settings().UpscaleTextures && application.ShowUpscaled ? (upscale, application.SupportsBc7) : null),
            // The game's BC1/BC3 textures go to the GPU as they are: a quarter of the memory, no decoding.
            // WADDAMBURO_DECODE_TEXTURES=1 decodes them all to RGBA as before (comparisons).
            KeepCompressed = Environment.GetEnvironmentVariable("WADDAMBURO_DECODE_TEXTURES") != "1"
                && application.Supports(TextureBlockFormat.Bc1) && application.Supports(TextureBlockFormat.Bc3),
        };
        if (upscale is not null)
        {
            upscale.LumenRoot = lumenRoot;
            source.Loaded = upscale.RecordUsed;
            SceneTextures.Upscaler = new TextureUpscaler(upscale, settings);
            Console.WriteLine($"Texture upscaling {(settings().UpscaleTextures ? "on" : "off")}, "
                + $"{settings().UpscaleThreads} background thread(s), cache {upscale.Cache}.");
        }
        return (source, upscale);
    }

    private static void patch(string archive, LumenMovieContent content, (UpscaleTool Tool, bool Bc7)? upscale)
    {
        // Pixel art: whole pixels, never the upscaler's guesses (cached ones included).
        if (PixelArt.IsArchive(archive))
        {
            content.DecodeAll();
            content.ReplaceTextures((texture, _) =>
            {
                if (ImmutableCollectionsMarshal.AsArray(texture.Rgba8) is not { Length: > 0 } pixels)
                    return texture;
                var (width, height, enlarged) = PixelArt.Enlarge(texture.Width, texture.Height, pixels);
                upscale?.Tool.MarkUpscaled(enlarged);
                return texture with { Width = width, Height = height, Rgba8 = ImmutableCollectionsMarshal.AsImmutableArray(enlarged) };
            });
            return;
        }
        var patching = SongSelectGenrePatch.AppliesTo(content);
        // The genre patch decodes only the textures it recolours (song_select decoded whole was 343 MiB, 337 ms).
        var started = Stopwatch.GetTimestamp();
        upscale?.Tool.ApplyCached(archive, content, upscale.Value.Bc7);
        var cached = Stopwatch.GetElapsedTime(started);
        if (!patching)
            return;
        var own = content.Textures.Length;
        SongSelectGenrePatch.Apply(content);
        if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1")
            Console.Error.WriteLine($"Profile patch {content.Name}: upscales {cached.TotalMilliseconds:F0} ms, "
                + $"genre patch {(Stopwatch.GetElapsedTime(started) - cached).TotalMilliseconds:F0} ms.");
        for (var index = own; index < content.Textures.Length; index++)
            if (ImmutableCollectionsMarshal.AsArray(content.Textures[index].Rgba8) is { } pixels)
                upscale?.Tool.MarkUpscaled(pixels);
    }
}
