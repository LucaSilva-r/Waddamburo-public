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
    private readonly record struct Kept(LumenMovieContent Content, RenderTextureId[] Textures, SceneId Scene, long Used);
    // Every texture alive, on screen or kept: past it the copies longest unused go. A Switch's GPU buffers are the limit
    // there (one a texture; the system refused the 4,872nd, and that texture came out missing), bgfx's handles elsewhere
    // (16,384). Room is left for what goes up before the released ones are gone, a frame later.
    private static readonly int MaximumTextures = PlatformInfo.IsSwitch ? 3600 : 14000;
    // Textures really uploaded per frame: the cost is on bgfx's render thread, which the time budget does not see.
    // ponytail: guessed counts; tune on hardware (a Switch's frames hitch past a few a frame).
    private static readonly int MovingUploads = PlatformInfo.IsSwitch ? 2 : 16, StillUploads = PlatformInfo.IsSwitch ? 8 : 64;
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

    /// <summary>Drops every kept copy (their files changed: the next load reads them again).</summary>
    public void ForgetKept()
    {
        foreach (var kept in _kept.Values.ToArray())
            unkeep(kept.Textures);
        _kept.Clear();
        dropWarming();
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
        dropWarming();
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
    /// <param name="warm">The reachable screens may load too (nothing moving, no song playing): see <see cref="Want"/>.</param>
    public void UploadAhead(bool still = false, bool warm = false)
    {
        var budget = still ? StillBudget : UploadAheadBudget;
        var start = Stopwatch.GetTimestamp();
        // A switch waits for its own prefetch (the attract's caution screen held 7.6 s for the demo at the moving cap):
        // only the screens ahead keep to the moving one.
        var first = SceneTextures.Uploads;
        bool full(int count) => Stopwatch.GetElapsedTime(start) > budget || SceneTextures.Uploads - first >= count;
        if (uploadPrefetches(() => full(StillUploads)) && warm)
            Warm(() => full(still ? StillUploads : MovingUploads));
    }

    // False when the frame's budget ran out first.
    private bool uploadPrefetches(Func<bool> full)
    {
        foreach (var (_, content) in movies.DecodedPrefetches)
        {
            if (_uploadedAhead.ContainsKey(content))
                continue;
            if (!_partial.ContainsKey(content))
                makeRoom(SceneTextures.Missing(content));
            var (uploaded, next) = _partial.GetValueOrDefault(content, (new RenderTextureId[content.Textures.Length], 0));
            for (; next < uploaded.Length; next++)
            {
                if (full())
                {
                    _partial[content] = (uploaded, next);
                    return false;
                }
                uploaded[next] = SceneTextures.Acquire(application, content, next);
            }
            _partial.Remove(content);
            content.ReleaseDecodedTexturePixels();
            _uploadedAhead[content] = uploaded;
        }
        return true;
    }

    // The screens the active one leads to (ScreenGraph), in order, loaded one movie at a time into the kept copies while
    // the game allows it. Depth 1 is protected from eviction and may evict for room; deeper ones (devices that preload)
    // only take free room, and the first that does not fit ends the deeper loading until the next screen.
    private readonly record struct Wanted((string ArchiveId, string MovieId) Key, SceneId Scene, int Depth);
    private Wanted[] _wanted = [];
    private HashSet<(string ArchiveId, string MovieId)> _protected = [], _skipped = [];
    private int _depthLimit = int.MaxValue;
    private bool _warmDone;
    private (Wanted Movie, Task<LumenMovieContent> Content)? _warming;
    private (Wanted Movie, LumenMovieContent Content, RenderTextureId[] Uploaded, int Next)? _warmUpload;

    /// <summary>The active screen's reachable screens with their depth (1: one switch away), the likeliest first.</summary>
    public void Want(IEnumerable<(SceneDefinition Scene, int Depth)> screens)
    {
        var seen = new HashSet<(string ArchiveId, string MovieId)>();
        _wanted = [.. screens.SelectMany(screen => keys(screen.Scene.Layers).Select(key => new Wanted(key, screen.Scene.Id, screen.Depth)))
            .Where(wanted => seen.Add(wanted.Key))];
        _protected = [.. _wanted.Where(static wanted => wanted.Depth == 1).Select(static wanted => wanted.Key)];
        _skipped = [];
        _depthLimit = int.MaxValue;
        _warmDone = false;
    }

    /// <summary>Every movie one switch away is up (or failed, or did not fit): a switch to any of them will not load.</summary>
    public bool WarmReady => _protected.All(key => _kept.ContainsKey(key) || _shown.Contains(key) || _skipped.Contains(key)
        || _prefetching.Contains(key)); // the switch's own prefetch loads those (PrefetchReady)

    private void Warm(Func<bool> full)
    {
        if (_warmUpload is { } upload)
        {
            for (; upload.Next < upload.Uploaded.Length; upload.Next++)
            {
                if (full())
                {
                    _warmUpload = upload;
                    return;
                }
                upload.Uploaded[upload.Next] = SceneTextures.Acquire(application, upload.Content, upload.Next);
            }
            upload.Content.ReleaseDecodedTexturePixels();
            if (_kept.Remove(upload.Movie.Key, out var old))
                unkeep(old.Textures);
            _kept[upload.Movie.Key] = new(upload.Content, upload.Uploaded, upload.Movie.Scene, ++_use);
            _warmUpload = null;
        }
        if (_warming is { } warming)
        {
            if (!warming.Content.IsCompleted)
                return;
            _warming = null;
            var movie = warming.Movie;
            if (!warming.Content.IsCompletedSuccessfully)
            {
                _skipped.Add(movie.Key); // its scene reports the error when it loads
                return;
            }
            var content = warming.Content.Result;
            var missing = SceneTextures.Missing(content);
            if (movie.Depth == 1)
                makeRoom(missing);
            if (application.TextureCount + missing > MaximumTextures)
            {
                _skipped.Add(movie.Key);
                if (movie.Depth > 1)
                    _depthLimit = movie.Depth;
                Console.WriteLine($"Screens ahead: {movie.Key.MovieId} ({missing} textures) does not fit; textures {application.TextureUsage}.");
                return;
            }
            _warmUpload = (movie, content, new RenderTextureId[content.Textures.Length], 0);
            return;
        }
        // A switch's own prefetch goes first.
        if (Prefetched is not null && !PrefetchReady)
            return;
        foreach (var wanted in _wanted)
            if (wanted.Depth < _depthLimit && !_kept.ContainsKey(wanted.Key) && !_shown.Contains(wanted.Key)
                && !_skipped.Contains(wanted.Key) && !_prefetching.Contains(wanted.Key))
            {
                _warming = (wanted, movies.Decode(wanted.Key.ArchiveId, wanted.Key.MovieId));
                return;
            }
        movies.ForgetDecodeArchive();
        if (!_warmDone)
            Console.WriteLine($"Screens ahead loaded: {_wanted.Count(wanted => _kept.ContainsKey(wanted.Key) || _shown.Contains(wanted.Key))} of "
                + $"{_wanted.Length} movies ({_skipped.Count} skipped); textures {application.TextureUsage}; " + heap());
        _warmDone = true;
    }

    // What the GC walks: heap, its large-object part (archives, pixels), collections and their pauses so far.
    private static string heap()
    {
        var info = GC.GetGCMemoryInfo();
        return FormattableString.Invariant($"heap {info.HeapSizeBytes / 1048576d:F0} MiB (LOH {info.GenerationInfo[3].SizeAfterBytes / 1048576d:F0}), ")
            + $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms.";
    }

    private void dropWarming()
    {
        if (_warmUpload is { } upload)
            SceneTextures.Release(application, upload.Uploaded.Take(upload.Next));
        _warmUpload = null;
        _warming = null;
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

    // Kept copies go until so many more textures fit, the longest unused first: never what is shown, being prefetched or
    // one switch away.
    private void makeRoom(int textures)
    {
        while (application.TextureCount + textures > MaximumTextures)
        {
            ((string ArchiveId, string MovieId) Key, Kept Copy)? oldest = null;
            foreach (var (key, copy) in _kept)
                if (!_shown.Contains(key) && !_prefetching.Contains(key) && !_protected.Contains(key)
                    && (oldest is null || copy.Used <= oldest.Value.Copy.Used))
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
            foreach (var (key, old) in _kept.Where(pair => pair.Value.Scene == keep.Id && !shown.Contains(pair.Key) && !_protected.Contains(pair.Key)).ToArray())
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

    /// <summary>A movie swapped into the shown scene (<see cref="LumenGameSceneInstance.Swap"/>): its textures after the scene's.</summary>
    public void AddSceneTextures(LumenMovieContent content) => _textures = [.. _textures, .. SceneTextures.Upload(application, content)];

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

    /// <summary>A held frame is on screen (presented at least once).</summary>
    public bool HoldShown => _heldFrame is not null && !_heldNotPresented;

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
        dropWarming();
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
