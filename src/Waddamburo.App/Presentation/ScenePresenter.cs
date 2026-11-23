using System.Diagnostics;
using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The active scene's textures on the GPU and what stays on screen across a scene switch: the old
/// scene's last frame (or black while loading) until the new one has drawn. The next scene's movies,
/// prefetched in the background, go up a few per frame so its switch does not stall.
/// </summary>
internal sealed class ScenePresenter(SdlApplication application, DirectoryLumenMovieContentSource movies, Func<bool>? upscaling = null) : IDisposable
{
    private static readonly TimeSpan UploadAheadBudget = TimeSpan.FromMilliseconds(4), StillBudget = TimeSpan.FromMilliseconds(12);
    private readonly Dictionary<LumenMovieContent, RenderTextureId[]> _uploadedAhead = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LumenMovieContent, (RenderTextureId[] Uploaded, int Next)> _partial = new(ReferenceEqualityComparer.Instance);
    // Screens' movies stay decoded, their textures up, between visits: a load not prefetched (leaving a song early, a
    // retry) reuses them instead of decoding again (~0.5 s; an SD card takes longer still).
    // Each copy remembers the scene that kept it: keeping that scene again drops the copies it no longer
    // shows (gameplay picks a random background/dancer mix per song; never evicting them leaked ~150
    // textures a song until bgfx ran out of handles).
    // Every list here (kept copies, prefetches, the scene's, the held frame's) holds its own references to its
    // textures (SceneTextures shares them by picture) and gives them back when it goes.
    private readonly Dictionary<(string ArchiveId, string MovieId), Kept> _kept = [];
    private readonly record struct Kept(LumenMovieContent Content, RenderTextureId[] Textures, SceneId Scene, long Used, bool Pinned = false);
    // Every texture alive, on screen or kept: past it the copies longest unused go. A Switch's GPU buffers are the limit
    // there (one a texture; the system refused the 4,872nd, and that texture came out missing), bgfx's handles elsewhere
    // (16,384). Room is left for what goes up before the released ones are gone, a frame later.
    private static readonly int MaximumTextures = PlatformInfo.IsSwitch ? 3600 : 14000;
    // What boot fills: the play's own screens fit on top of it without dropping any of it at once.
    private const int MaximumPreloadedTextures = 2400;
    private long _use;
    private HashSet<(string ArchiveId, string MovieId)> _shown = [], _prefetching = [];
    // Copies hold the upscales as they were when decoded: the setting changing drops them all.
    private bool? _keptUpscaled;
    private RenderTextureId[] _textures = [];
    private RenderTextureId[] _heldTextures = [];
    private RenderFrame? _heldFrame;
    private bool _heldBlack; // the loading frame: an intermission still shown is drawn over it
    private RenderFrame? _lastFrame;
    private bool _lastHadIntermission;
    private int _holdUntilTick;
    private bool _heldNotPresented;

    /// <summary>The active scene's textures, by the scene's texture index.</summary>
    public RenderTextureId[] Textures => _textures;

    /// <summary>Starts decoding a scene's movies in the background; their textures go up between frames.</summary>
    public void Prefetch(SceneDefinition scene)
    {
        releaseUploadedAhead();
        dropStale();
        // What is kept needs no decoding.
        var missing = keys(scene.Layers).Where(key => !_kept.ContainsKey(key)).Distinct().ToArray();
        movies.Prefetch(missing);
        Prefetched = scene.Id;
        _prefetching = [.. keys(scene.Layers)];
        Console.WriteLine($"Prefetching {scene.Id} ({missing.Length} of {scene.Layers.Length} movies).");
    }

    /// <summary>
    /// Loads a scene's movies and keeps them for good, whatever scene shows what (at boot: <paramref name="draw"/> draws a
    /// frame meanwhile). False when they are too many to keep: the scene loads at its first visit, as without this. A movie
    /// that fails to load is left for its scene to report.
    /// </summary>
    public bool Preload(SceneDefinition scene, Action draw)
    {
        Prefetch(scene);
        for (; !movies.PrefetchComplete; Thread.Sleep(5))
            draw();
        var loaded = movies.DecodedPrefetches.ToArray();
        var fits = application.TextureCount + loaded.Sum(static movie => SceneTextures.Missing(movie.Content)) <= MaximumPreloadedTextures;
        while (fits && !PrefetchReady)
        {
            UploadAhead(still: true);
            draw();
        }
        foreach (var (key, content) in fits ? loaded : [])
        {
            _uploadedAhead.Remove(content, out var textures);
            _kept[key] = new(content, textures!, scene.Id, Used: 0, Pinned: true);
        }
        movies.Prefetch([]);
        releaseUploadedAhead();
        Prefetched = null;
        _prefetching = [];
        return fits;
    }

    private void dropStale()
    {
        var now = upscaling?.Invoke() == true;
        if (_keptUpscaled == now)
            return;
        _keptUpscaled = now;
        foreach (var kept in _kept.Values.ToArray())
            unkeep(kept.Textures);
        _kept.Clear();
    }

    /// <summary>The scene being prefetched (null once a scene took it).</summary>
    public SceneId? Prefetched { get; private set; }

    /// <summary>Every prefetched movie is decoded and uploaded ahead.</summary>
    public bool PrefetchReady => movies.PrefetchComplete && movies.DecodedPrefetches.All(movie => _uploadedAhead.ContainsKey(movie.Content));

    /// <summary>
    /// Uploads decoded prefetches within a small time budget (once per frame), a texture at a time: a
    /// whole movie at once stalled the frame (and the audio) as Song Select's course select opened.
    /// </summary>
    /// <param name="still">Nothing moves on screen (the rainbow holding its title): frames may take longer.</param>
    public void UploadAhead(bool still = false)
    {
        var budget = still ? StillBudget : UploadAheadBudget;
        var start = Stopwatch.GetTimestamp();
        foreach (var (_, content) in movies.DecodedPrefetches)
        {
            if (_uploadedAhead.ContainsKey(content))
                continue;
            if (!_partial.ContainsKey(content))
                makeRoom(SceneTextures.Missing(content));
            var (uploaded, next) = _partial.GetValueOrDefault(content, (new RenderTextureId[content.Textures.Length], 0));
            for (; next < uploaded.Length; next++)
            {
                if (Stopwatch.GetElapsedTime(start) > budget)
                {
                    _partial[content] = (uploaded, next);
                    return;
                }
                uploaded[next] = SceneTextures.Acquire(application, content, next);
            }
            _partial.Remove(content);
            content.ReleaseDecodedTexturePixels();
            _uploadedAhead[content] = uploaded;
        }
    }

    /// <summary>
    /// A scene switch is loading: only its loads reuse kept copies (an overlay's or the indicators' own
    /// upload would find their pixels gone).
    /// </summary>
    public bool Switching { get; set; }

    /// <summary>The kept copy of a movie, for the content source (the loading thread; the main one waits).</summary>
    public LumenMovieContent? Reuse(string archiveId, string movieId) =>
        Switching && _kept.TryGetValue((archiveId, movieId), out var kept) ? kept.Content : null;

    private static IEnumerable<(string ArchiveId, string MovieId)> keys(IEnumerable<SceneLayerDefinition> layers) =>
        layers.Select(static layer => (layer.ArchiveId, layer.MovieId));

    // Kept copies go until so many more textures fit, the longest unused first (of the preloaded ones, the last in the
    // list): never what is shown or being prefetched.
    private void makeRoom(int textures)
    {
        while (application.TextureCount + textures > MaximumTextures)
        {
            ((string ArchiveId, string MovieId) Key, Kept Copy)? oldest = null;
            foreach (var (key, copy) in _kept)
                if (!_shown.Contains(key) && !_prefetching.Contains(key) && (oldest is null || copy.Used <= oldest.Value.Copy.Used))
                    oldest = (key, copy);
            if (oldest is not { } drop)
                return;
            _kept.Remove(drop.Key);
            unkeep(drop.Copy.Textures);
        }
    }

    // A movie half uploaded ahead: the scene switch uploads the rest.
    private RenderTextureId[]? takeAhead(LumenMovieContent content)
    {
        foreach (var kept in _kept.Values)
            if (ReferenceEquals(kept.Content, content))
            {
                SceneTextures.Retain(kept.Textures);
                return kept.Textures;
            }
        if (_uploadedAhead.Remove(content, out var textures))
            return textures;
        if (!_partial.Remove(content, out var partial))
            return null;
        SceneTextures.UploadRest(application, content, partial.Uploaded, partial.Next);
        return partial.Uploaded;
    }

    /// <summary>
    /// A switch starts: the last frame stays up until the new scene has drawn (two ticks, and at least
    /// once), its textures kept alive. A cleared intermission may already have released its textures, so
    /// <paramref name="black"/> (or an intermission in the last frame) holds black with
    /// <paramref name="loadingFrame"/>'s network icon instead, as the original game did while loading.
    /// </summary>
    public void Hold(int tick, bool black, Func<RenderFrame> loadingFrame)
    {
        if (_heldFrame is null && _lastFrame is not null)
        {
            _heldBlack = black || _lastHadIntermission;
            if (_heldBlack)
                _heldFrame = loadingFrame();
            else
            {
                _heldFrame = _lastFrame.Detached(); // the frame's quads are reused by the next frame
                _heldTextures = _textures;
                SceneTextures.Retain(_heldTextures);
            }
        }
        _holdUntilTick = tick + 2;
        _heldNotPresented = true;
    }

    /// <summary>The old scene is gone: its textures go, unless its held frame still shows them.</summary>
    public void ReleaseScene(LumenGameSceneInstance? keep = null)
    {
        dropStale();
        if (keep is not null)
        {
            var shown = keep.Layers.Select(static layer => (layer.Definition.ArchiveId, layer.Definition.MovieId)).ToHashSet();
            foreach (var (key, old) in _kept.Where(pair => pair.Value.Scene == keep.Id && !shown.Contains(pair.Key) && !pair.Value.Pinned).ToArray())
            {
                _kept.Remove(key);
                unkeep(old.Textures);
            }
        }
        foreach (var layer in keep?.Layers ?? [])
        {
            var key = (layer.Definition.ArchiveId, layer.Definition.MovieId);
            if (_kept.TryGetValue(key, out var old))
            {
                if (ReferenceEquals(old.Content, layer.Content))
                {
                    _kept[key] = old with { Used = ++_use };
                    continue;
                }
                unkeep(old.Textures);
            }
            var textures = _textures[(int)layer.TextureOffset..(int)(layer.TextureOffset + layer.TextureCount)];
            SceneTextures.Retain(textures);
            _kept[key] = new(layer.Content, textures, keep!.Id, ++_use);
        }
        SceneTextures.Release(application, _textures);
        _textures = [];
    }

    // No longer kept: what nothing else holds goes now.
    private void unkeep(RenderTextureId[] textures) => SceneTextures.Release(application, textures);

    /// <summary>The new scene's textures: its prefetched uploads where there are any, the rest now.</summary>
    public void Activate(LumenGameSceneInstance scene)
    {
        var ahead = _uploadedAhead.Count + _partial.Count;
        var decoded = movies.DecodedPrefetches.Count();
        _shown = [.. keys(scene.Layers.Select(static layer => layer.Definition))];
        // What is neither kept nor up already goes up now.
        makeRoom(scene.Layers.Select(static layer => layer.Content).Distinct()
            .Where(content => !_uploadedAhead.ContainsKey(content) && !_kept.Values.Any(copy => ReferenceEquals(copy.Content, content)))
            .Sum(SceneTextures.Missing));
        _textures = SceneTextures.Upload(application, scene, takeAhead);
        Console.WriteLine($"Scene {scene.Id}: {ahead - _uploadedAhead.Count - _partial.Count} of {scene.Layers.Length} movies "
            + $"uploaded ahead ({ahead} ready, {decoded} decoded ahead); textures {application.TextureUsage}.");
        if (Prefetched == scene.Id || ahead - _uploadedAhead.Count - _partial.Count > 0)
        {
            releaseUploadedAhead(); // this scene took its prefetch; the rest is unused
            Prefetched = null;
            _prefetching = [];
        }
    }

    /// <summary>
    /// The frame held across a switch while it must stay (Black: the loading frame), or null once the
    /// new scene can draw (the held textures are released then).
    /// </summary>
    public (RenderFrame Frame, bool Black)? Held(int tick)
    {
        if (_heldFrame is not { } held)
            return null;
        var first = _heldNotPresented;
        _heldNotPresented = false;
        if (first || tick < _holdUntilTick)
            return (held, _heldBlack);
        // Results can show the held gameplay frame's name boards: those stay with the active scene.
        SceneTextures.Release(application, _heldTextures);
        _heldTextures = [];
        _heldFrame = null;
        return null;
    }

    /// <summary>The frame just drawn (what a switch holds), and whether an intermission was in it.</summary>
    public void Presented(RenderFrame frame, bool withIntermission)
    {
        _lastFrame = frame;
        _lastHadIntermission = withIntermission;
    }

    public void Dispose()
    {
        SceneTextures.Release(application, _textures.Concat(_heldTextures).Concat(_kept.Values.SelectMany(static kept => kept.Textures)));
        releaseUploadedAhead();
    }

    private void releaseUploadedAhead()
    {
        foreach (var textures in _uploadedAhead.Values)
            SceneTextures.Release(application, textures);
        foreach (var (uploaded, next) in _partial.Values)
            SceneTextures.Release(application, uploaded.Take(next));
        // Their pixels went with the upload: a later load of them must decode afresh.
        movies.Forget(_uploadedAhead.Keys);
        _uploadedAhead.Clear();
        _partial.Clear(); // not yet released: the prefetch still holds them whole
    }
}
