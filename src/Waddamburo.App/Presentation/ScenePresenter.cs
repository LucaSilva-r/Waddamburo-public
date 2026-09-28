using System.Diagnostics;
using Waddamburo.Game;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The active scene's textures on the GPU and what stays on screen across a scene switch: the old
/// scene's last frame (or black while loading) until the new one has drawn. The next scene's movies,
/// prefetched in the background, go up a few per frame so its switch does not stall.
/// </summary>
internal sealed class ScenePresenter(SdlApplication application, DirectoryLumenMovieContentSource movies) : IDisposable
{
    private static readonly TimeSpan UploadAheadBudget = TimeSpan.FromMilliseconds(4);
    private readonly Dictionary<LumenMovieContent, RenderTextureId[]> _uploadedAhead = new(ReferenceEqualityComparer.Instance);
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
        movies.Prefetch(scene.Layers.Select(static layer => (layer.ArchiveId, layer.MovieId)));
    }

    /// <summary>Uploads decoded prefetches within a small time budget (once per frame).</summary>
    // ponytail: one movie per step once over budget; a single huge movie can still take a frame.
    public void UploadAhead()
    {
        var start = Stopwatch.GetTimestamp();
        foreach (var content in movies.DecodedPrefetches)
        {
            if (Stopwatch.GetElapsedTime(start) > UploadAheadBudget)
                return;
            if (!_uploadedAhead.ContainsKey(content))
                _uploadedAhead[content] = SceneTextures.Upload(application, content);
        }
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
                _heldFrame = _lastFrame;
                _heldTextures = _textures;
            }
        }
        _holdUntilTick = tick + 2;
        _heldNotPresented = true;
    }

    /// <summary>The old scene is gone: its textures go, unless its held frame still shows them.</summary>
    public void ReleaseScene()
    {
        if (!ReferenceEquals(_heldTextures, _textures))
            SceneTextures.Release(application, _textures);
        _textures = [];
    }

    /// <summary>The new scene's textures: its prefetched uploads where there are any, the rest now.</summary>
    public void Activate(LumenGameSceneInstance scene)
    {
        var ahead = _uploadedAhead.Count;
        _textures = SceneTextures.Upload(application, scene,
            content => _uploadedAhead.Remove(content, out var textures) ? textures : null);
        if (ahead - _uploadedAhead.Count > 0)
            releaseUploadedAhead(); // this scene took its prefetch; the rest is unused
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
        SceneTextures.Release(application, _textures);
        SceneTextures.Release(application, _heldTextures);
        releaseUploadedAhead();
    }

    private void releaseUploadedAhead()
    {
        foreach (var textures in _uploadedAhead.Values)
            SceneTextures.Release(application, textures);
        _uploadedAhead.Clear();
    }
}
