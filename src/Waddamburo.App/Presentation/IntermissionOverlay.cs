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
/// Preloaded ones stay loaded and are only hidden: showing them never touches the disk.
/// </summary>
internal sealed class IntermissionOverlay(SdlApplication application, LumenGameSceneLoader loader) : IDisposable
{
    private LumenGameSceneInstance? _scene;
    private RenderTextureId[] _textures = [];
    private readonly Dictionary<SceneDefinition, (LumenGameSceneInstance Scene, RenderTextureId[] Textures)> _kept =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Loads an intermission once and keeps it (the rainbow must never stall a song's start).</summary>
    public void Preload(SceneDefinition definition)
    {
        var scene = (LumenGameSceneInstance)loader.LoadAsync(definition, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        _kept[definition] = (scene, SceneTextures.Upload(application, scene));
    }

    public bool IsShown => _scene is not null;

    /// <summary>The shown movie's player, or null.</summary>
    public LumenPlayer? Player => _scene?.Player.Layers.Single().Player;

    public LumenPlayer Show(SceneDefinition definition)
    {
        Clear();
        if (_kept.TryGetValue(definition, out var kept))
            (_scene, _textures) = kept;
        else
        {
            _scene = (LumenGameSceneInstance)loader.LoadAsync(definition, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _textures = SceneTextures.Upload(application, _scene);
        }
        return Player!;
    }

    public void Clear()
    {
        if (_scene is not null && !_kept.Values.Any(kept => ReferenceEquals(kept.Scene, _scene)))
        {
            SceneTextures.Release(application, _textures);
            _scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _textures = [];
        _scene = null;
    }

    /// <summary>Ticks the intermission advances per game tick (a quick rainbow for search results).</summary>
    public int Speed { get; set; } = 1;

    public void Advance()
    {
        for (var tick = 0; tick < Speed; tick++)
            _scene?.Player.Advance();
    }

    public IEnumerable<RenderQuad> Quads(float interpolation, Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        _scene is null ? [] : SceneTextures.Compose(_scene.Player.CreateRenderSnapshot(interpolation), _textures, "Intermission",
            surfaces).Quads;

    public void Dispose()
    {
        Clear();
        foreach (var (scene, textures) in _kept.Values)
        {
            SceneTextures.Release(application, textures);
            scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _kept.Clear();
    }
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
        UploadRest(application, content, uploaded, 0);
        return uploaded;
    }

    /// <summary>Uploads a movie's textures from <paramref name="from"/> on, then drops its pixels.</summary>
    public static void UploadRest(SdlApplication application, LumenMovieContent content, RenderTextureId[] uploaded, int from)
    {
        for (var index = from; index < uploaded.Length; index++)
            uploaded[index] = UploadOne(application, content.Textures[index]);
        content.ReleaseDecodedTexturePixels();
    }

    /// <summary>One texture: BC7 blocks as they are, RGBA queued for upscaling.</summary>
    public static RenderTextureId UploadOne(SdlApplication application, LumenTextureContent texture)
    {
        var (width, height) = (checked((uint)texture.Width), checked((uint)texture.Height));
        if (!texture.Bc7.IsEmpty)
            return application.UploadBc7(width, height, texture.Bc7.Span);
        var rgba = ImmutableCollectionsMarshal.AsArray(texture.Rgba8)
            ?? throw new InvalidDataException("Texture pixels are unavailable.");
        var id = application.UploadRgba8(width, height, rgba);
        // The upscaler keeps the pixel arrays the content is about to drop.
        Upscaler?.Enqueue(id, width, height, rgba);
        return id;
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
        // Distinct: a movie shown by two layers of a scene shares one kept copy, so its ids appear twice.
        foreach (var texture in textures.Distinct())
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
