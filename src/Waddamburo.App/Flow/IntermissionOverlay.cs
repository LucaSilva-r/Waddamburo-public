using System.Diagnostics;
using System.Runtime.InteropServices;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

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
    public static RenderTextureId[] Upload(SdlApplication application, LumenGameSceneInstance scene)
    {
        var profile = Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";
        var start = profile ? Stopwatch.GetTimestamp() : 0;
        var uploads = scene.Textures.Select(static texture => new RgbaTextureUpload(
            checked((uint)texture.Width), checked((uint)texture.Height),
            ImmutableCollectionsMarshal.AsArray(texture.Rgba8)
                ?? throw new InvalidDataException("Texture pixels are unavailable."))).ToArray();
        var uploaded = application.UploadRgba8Batch(uploads);
        if (profile)
        {
            var rgbaBytes = scene.Textures.Sum(static texture => (long)texture.Rgba8.Length);
            using var process = Process.GetCurrentProcess();
            Console.Error.WriteLine($"Profile scene {scene.Id}: {uploaded.Length} textures, "
                + $"RGBA {rgbaBytes / 1048576d:F1} MiB, "
                + $"upload {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms, "
                + $"managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB, "
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
            RenderColor.WaddamburoBlue,
            index => index < textures.Length
                ? textures[index]
                : throw new InvalidDataException($"{owner} snapshot references missing texture {index}."),
            surfaces);
}
