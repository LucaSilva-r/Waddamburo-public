using System.Diagnostics;
using System.Runtime.InteropServices;
using Waddamburo.Game;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The one intermission movie drawn over the scene (traced depth -2000): the rainbow before a song,
/// the shutter after it, the fade between the credit's closing scenes. Showing one replaces the last.
/// </summary>
internal sealed class IntermissionOverlay(SdlApplication application, LumenGameSceneLoader loader) : IDisposable
{
    private LumenGameSceneInstance? _scene;
    private RenderTextureId[] _textures = [];

    public bool IsShown => _scene is not null;

    /// <summary>The shown movie's player, or null.</summary>
    public LumenPlayer? Player => _scene?.Player.Layers.Single().Player;

    public LumenPlayer Show(SceneDefinition definition)
    {
        Clear();
        _scene = (LumenGameSceneInstance)loader.LoadAsync(definition, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        _textures = SceneTextures.Upload(application, _scene);
        return Player!;
    }

    public void Clear()
    {
        SceneTextures.Release(application, _textures);
        _textures = [];
        _scene?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _scene = null;
    }

    public void Advance() => _scene?.Player.Advance();

    public IEnumerable<RenderQuad> Quads(float interpolation, Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        _scene is null ? [] : SceneTextures.Compose(_scene.Player.CreateRenderSnapshot(interpolation), _textures, "Intermission",
            surfaces).Quads;

    public void Dispose() => Clear();
}

/// <summary>Uploads a loaded scene's texture atlas and draws its snapshots.</summary>
internal static class SceneTextures
{
    /// <summary>Upscales movie textures in the background once they are up (null: not installed).</summary>
    // ponytail: static, set once by the shell; every movie texture goes through Upload below.
    public static TextureUpscaler? Upscaler { get; set; }

    /// <summary>One movie's textures (the scene switch then only uploads what was not done ahead).</summary>
    public static RenderTextureId[] Upload(SdlApplication application, LumenMovieContent content)
    {
        var uploaded = new RenderTextureId[content.Textures.Length];
        for (var index = 0; index < uploaded.Length; index++)
        {
            var texture = content.Textures[index];
            var (width, height) = (checked((uint)texture.Width), checked((uint)texture.Height));
            if (!texture.Bc7.IsEmpty)
            {
                uploaded[index] = application.UploadBc7(width, height, texture.Bc7.Span);
                continue;
            }
            var rgba = ImmutableCollectionsMarshal.AsArray(texture.Rgba8)
                ?? throw new InvalidDataException("Texture pixels are unavailable.");
            uploaded[index] = application.UploadRgba8(width, height, rgba);
            // The upscaler keeps the pixel arrays the content is about to drop.
            Upscaler?.Enqueue(uploaded[index], width, height, rgba);
        }
        content.ReleaseDecodedTexturePixels();
        return uploaded;
    }

    public static RenderTextureId[] Upload(SdlApplication application, LumenGameSceneInstance scene,
        Func<LumenMovieContent, RenderTextureId[]?>? uploadedAhead = null)
    {
        var profile = Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";
        var start = profile ? Stopwatch.GetTimestamp() : 0;
        var rgbaBytes = scene.Textures.Sum(static texture => (long)texture.Rgba8.Length + texture.Bc7.Length);
        var uploaded = scene.Layers.SelectMany(layer => uploadedAhead?.Invoke(layer.Content) ?? Upload(application, layer.Content))
            .ToArray();
        if (profile)
        {
            using var process = Process.GetCurrentProcess();
            Console.Error.WriteLine($"Profile scene {scene.Id}: {uploaded.Length} textures, "
                + $"pixels {rgbaBytes / 1048576d:F1} MiB, "
                + $"upload {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms, "
                + $"managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB (committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d:F1}), "
                + $"RSS {process.WorkingSet64 / 1048576d:F1} MiB.");
        }
        scene.ReleaseUploadedTexturePixels();
        return uploaded;
    }

    public static void Release(SdlApplication application, IEnumerable<RenderTextureId> textures)
    {
        foreach (var texture in textures)
            application.ReleaseTexture(texture);
    }

    public static RenderFrame Compose(LumenRenderSnapshot snapshot, RenderTextureId[] textures, string owner,
        Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        LumenRenderFrameAdapter.Compose(
            snapshot,
            RenderColor.Black,
            index => index < textures.Length
                ? textures[index]
                : throw new InvalidDataException($"{owner} snapshot references missing texture {index}."),
            surfaces);
}
