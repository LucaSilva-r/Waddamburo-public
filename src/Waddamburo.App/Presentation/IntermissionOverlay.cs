using System.Diagnostics;
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
internal sealed class IntermissionOverlay(SdlApplication application, LumenGameSceneLoader loader, Waddamburo.Game.ISkin skin) : IDisposable
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
        // A kept movie starts each showing without a phase: a cover after its last cover is still told.
        Player!.Publish("transition.phase", LumenHostValue.Undefined);
        return Player!;
    }

    /// <summary>
    /// The shown movie covers the screen (transition.title, transition.side, then transition.phase "cover"); the
    /// game's by their labels or Close. False while it cannot take it yet (the game's shutter, before its first frame).
    /// </summary>
    /// <param name="title">The song's title shows on it (the rainbow before a song).</param>
    /// <param name="side">Whose colour: 0 the left drum, 1 the right, 2 both.</param>
    public bool Cover(bool title = false, int side = 0)
    {
        if (Player is not { } movie)
            return false;
        movie.Publish("transition.title", LumenHostValue.FromBoolean(title));
        movie.Publish("transition.side", LumenHostValue.FromNumber(side));
        movie.Publish("transition.phase", LumenHostValue.FromString("cover"));
        return skin.Transitions.Cover(movie, title, side);
    }

    /// <summary>The shown movie uncovers what is under it (transition.phase "reveal").</summary>
    public void Reveal(bool title = false)
    {
        if (Player is not { } movie)
            return;
        movie.Publish("transition.title", LumenHostValue.FromBoolean(title));
        movie.Publish("transition.phase", LumenHostValue.FromString("reveal"));
        skin.Transitions.Reveal(movie, title);
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

    private readonly LumenQuadBuffer _quads = new();

    public IEnumerable<RenderQuad> Quads(float interpolation, Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        _scene is null ? [] : SceneTextures.Compose(_scene.Player.CreateRenderSnapshot(interpolation, buffer: _quads), _textures, "Intermission",
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
            uploaded[index] = Acquire(application, content, index);
        content.ReleaseDecodedTexturePixels();
    }

    // Movie textures shared by picture: one GPU texture for every movie with the same one (the game's packages repeat
    // ~44% of theirs, Song Select's four variants ~780 each), alive while anything holds it. Each holder (a scene's
    // texture list, a kept copy, a prefetch, a held frame) holds one reference per entry and gives each back with
    // Release.
    // ponytail: static and main thread only, like Upscaler.
    private static readonly Dictionary<UInt128, RenderTextureId> _byHash = [];
    private static readonly Dictionary<RenderTextureId, (UInt128 Hash, int References)> _held = [];

    // The SVG textures up (each drawn for the pixels a unit it was made at), and the drawing again for a resize.
    private sealed record SvgTexture(byte[] Svg, (float Width, float Height) Units, float PixelsPerUnit);
    private static readonly Dictionary<RenderTextureId, SvgTexture> _svgs = [];
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(RenderTextureId Id, float PixelsPerUnit, (int Width, int Height, byte[] Rgba)? Image)> _sharpened = new();
    private static readonly HashSet<RenderTextureId> _sharpening = [];
    private static float _wanted;
    private static long _wantedSince;

    /// <summary>
    /// Each frame (main thread): once the screen's pixels per unit have held for a moment after a change (a resize
    /// settled), the SVG textures up are drawn again for them in the background and swapped in as they are done.
    /// </summary>
    public static void Sharpen(SdlApplication application, float pixelsPerUnit)
    {
        while (_sharpened.TryDequeue(out var done))
        {
            _sharpening.Remove(done.Id);
            // Not drawn (not an SVG after all): kept as it is, not tried again for these pixels a unit.
            if (_svgs.TryGetValue(done.Id, out var svg)
                && (done.Image is not { } image || application.TryReplaceRgba8(done.Id, (uint)image.Width, (uint)image.Height, image.Rgba)))
                _svgs[done.Id] = svg with { PixelsPerUnit = done.PixelsPerUnit };
        }
        if (MathF.Abs(pixelsPerUnit - _wanted) > 0.01f)
        {
            (_wanted, _wantedSince) = (pixelsPerUnit, System.Diagnostics.Stopwatch.GetTimestamp());
            return;
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_wantedSince).TotalMilliseconds < 300)
            return;
        foreach (var (id, svg) in _svgs)
        {
            // Within 5%: as sharp as it gets; a texture of one flat colour stays small (SvgTextures.DecodeAt).
            if (MathF.Abs(svg.PixelsPerUnit - pixelsPerUnit) <= 0.05f * pixelsPerUnit || !_sharpening.Add(id))
                continue;
            var (bytes, units, wanted) = (svg.Svg, svg.Units, pixelsPerUnit);
            _ = Task.Run(() => _sharpened.Enqueue((id, wanted,
                SvgTextures.DecodeAt(bytes, LumenMovieContent.SvgPixels(units.Width, wanted), LumenMovieContent.SvgPixels(units.Height, wanted)))));
        }
    }

    /// <summary>Textures really uploaded so far (a shared one costs nothing): per-frame upload limits count these.</summary>
    public static long Uploads { get; private set; }

    /// <summary>A movie's texture: the GPU copy already up for the same picture, or uploaded now.</summary>
    public static RenderTextureId Acquire(SdlApplication application, LumenMovieContent content, int index)
    {
        var hash = content.TextureHash(index);
        if (_byHash.TryGetValue(hash, out var shared))
        {
            _held[shared] = (hash, _held[shared].References + 1);
            return shared;
        }
        // An SVG drawn for the screen stays out of the atlas: a resize draws it again at another size (Sharpen).
        var id = UploadOne(application, content.Textures[index], atlas: !content.TextureTiles(index) && content.Textures[index].Svg is null);
        Uploads++;
        _byHash[hash] = id;
        _held[id] = (hash, 1);
        return id;
    }

    /// <summary>How many of a movie's textures would really upload (none of their pictures is up yet).</summary>
    public static int Missing(LumenMovieContent content) =>
        Enumerable.Range(0, content.Textures.Length).Select(content.TextureHash).Distinct().Count(hash => !_byHash.ContainsKey(hash));

    /// <summary>One more reference to each of these (a second holder of the same list).</summary>
    public static void Retain(IEnumerable<RenderTextureId> textures)
    {
        foreach (var texture in textures)
            if (_held.TryGetValue(texture, out var held))
                _held[texture] = held with { References = held.References + 1 };
    }

    /// <summary>
    /// One texture: BC7 (an upscale) and the game's own BC1/BC3 blocks as they are, RGBA otherwise; the
    /// originals are queued for upscaling. <c>atlas</c>: may share an atlas page (a movie texture never drawn tiled).
    /// </summary>
    public static RenderTextureId UploadOne(SdlApplication application, LumenTextureContent texture, bool atlas = false)
    {
        var (width, height) = (checked((uint)texture.Width), checked((uint)texture.Height));
        if (!texture.Bc7.IsEmpty)
            return application.UploadBc7(width, height, texture.Bc7.Span);
        var format = texture.BlockFormat == LumenBlockFormat.Bc1 ? TextureBlockFormat.Bc1 : TextureBlockFormat.Bc3;
        if (!texture.Blocks.IsEmpty && application.Supports(format))
        {
            var compressed = application.UploadBlocks(format, width, height, texture.Blocks.Span, atlas);
            // A queued texture keeps its own copy of the blocks (a slice would keep its whole archive alive),
            // decoded only if the upscaler gets to it.
            if (texture.Upscalable && Upscaler is { } queue && queue.Wants(width, height))
            {
                var own = texture with { Blocks = texture.Blocks.ToArray() };
                queue.Enqueue(compressed, width, height, own.Pixels, texture.UpscaleKey);
            }
            return compressed;
        }
        var rgba = texture.Pixels();
        var id = application.UploadRgba8(width, height, rgba, atlas);
        if (texture.Svg is { } svg)
            _svgs[id] = new SvgTexture(svg, texture.ShownUnits, GameMovieContent.PixelsPerUnit);
        // The upscaler keeps the pixel arrays the content is about to drop.
        if (texture.Upscalable && Upscaler is { } upscaler && !upscaler.IsUpscaled(rgba))
            upscaler.Enqueue(id, width, height, () => rgba, texture.UpscaleKey);
        return id;
    }

    public static RenderTextureId[] Upload(SdlApplication application, LumenGameSceneInstance scene,
        Func<LumenMovieContent, RenderTextureId[]?>? uploadedAhead = null)
    {
        var profile = DiagnosticSwitches.Profile;
        var start = profile ? Stopwatch.GetTimestamp() : 0;
        var rgbaBytes = scene.Textures.Sum(static texture => (long)texture.Rgba8.Length + texture.Bc7.Length + texture.Blocks.Length);
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

    /// <summary>Gives back one reference per entry: a shared texture goes with its last one, any other at once.</summary>
    public static void Release(SdlApplication application, IEnumerable<RenderTextureId> textures)
    {
        foreach (var texture in textures)
        {
            if (_held.TryGetValue(texture, out var held))
            {
                if (held.References > 1)
                {
                    _held[texture] = held with { References = held.References - 1 };
                    continue;
                }
                _held.Remove(texture);
                _byHash.Remove(held.Hash);
            }
            _svgs.Remove(texture);
            application.ReleaseTexture(texture);
        }
    }

    public static RenderFrame Compose(LumenRenderSnapshot snapshot, RenderTextureId[] textures, string owner,
        Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        LumenRenderFrameAdapter.Compose(snapshot, RenderColor.Black, resolver(textures, owner), surfaces);

    /// <summary><see cref="Compose"/> appending to a reused list.</summary>
    public static void ComposeInto(LumenRenderSnapshot snapshot, List<RenderQuad> into, RenderTextureId[] textures, string owner,
        Func<LumenNativeSurfaceKey, RenderTextureId?> surfaces) =>
        LumenRenderFrameAdapter.ComposeInto(snapshot, into, resolver(textures, owner), surfaces);

    private static Func<uint, RenderTextureId> resolver(RenderTextureId[] textures, string owner) =>
        index => index < textures.Length
            ? textures[index]
            : throw new InvalidDataException($"{owner} snapshot references missing texture {index}.");
}
