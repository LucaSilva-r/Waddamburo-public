using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

internal sealed class SongTitleTextureCache : ISongBoardTextureService, IDisposable
{
    private const int MaximumResidentTextures = 64;
    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly Dictionary<LumenNativeSurfaceKey, TitleRequest> _requests = [];
    private readonly Dictionary<LumenNativeSurfaceKey, ResidentTexture> _resident = [];
    private readonly LinkedList<LumenNativeSurfaceKey> _leastRecentlyUsed = [];

    public SongTitleTextureCache(SdlApplication application, string fontPath)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        _fontPath = Path.GetFullPath(fontPath);
        if (!File.Exists(_fontPath))
            throw new FileNotFoundException("The configured Song Select font does not exist.", _fontPath);
    }

    public LumenNativeSurfaceKey GetSongTitle(SongSelectSong song, SongBoardTextureKind kind)
    {
        ArgumentNullException.ThrowIfNull(song);
        var key = new LumenNativeSurfaceKey($"song-title:{song.Descriptor.Key}:{kind}");
        var dimensions = kind switch
        {
            SongBoardTextureKind.Compact => (Width: 56U, Height: 400U),
            SongBoardTextureKind.Expanded => (Width: 96U, Height: 400U),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var request = new TitleRequest(song.Descriptor.Title.Primary, dimensions.Width, dimensions.Height);
        if (_requests.TryGetValue(key, out var existing) && existing != request)
            throw new InvalidOperationException($"Native surface key '{key}' was assigned conflicting content.");
        _requests[key] = request;
        return key;
    }

    public RenderTextureId Resolve(LumenNativeSurfaceKey key)
    {
        if (_resident.TryGetValue(key, out var cached))
        {
            touch(key, cached.Node);
            return cached.Texture;
        }
        if (!_requests.TryGetValue(key, out var request))
            throw new KeyNotFoundException($"No title content is registered for native surface '{key}'.");

        var surface = NativeVerticalTextRasterizer.Render(_fontPath, request.Text, request.Width, request.Height);
        var texture = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
        var node = _leastRecentlyUsed.AddFirst(key);
        _resident.Add(key, new ResidentTexture(texture, node));
        evictIfNeeded();
        return texture;
    }

    public void Dispose()
    {
        foreach (var texture in _resident.Values)
            _application.ReleaseTexture(texture.Texture);
        _resident.Clear();
        _leastRecentlyUsed.Clear();
    }

    private void touch(LumenNativeSurfaceKey key, LinkedListNode<LumenNativeSurfaceKey> node)
    {
        _leastRecentlyUsed.Remove(node);
        _resident[key] = _resident[key] with { Node = _leastRecentlyUsed.AddFirst(key) };
    }

    private void evictIfNeeded()
    {
        while (_resident.Count > MaximumResidentTextures)
        {
            var node = _leastRecentlyUsed.Last!;
            _leastRecentlyUsed.RemoveLast();
            var texture = _resident[node.Value].Texture;
            _resident.Remove(node.Value);
            _application.ReleaseTexture(texture);
        }
    }

    private sealed record TitleRequest(string Text, uint Width, uint Height);
    private sealed record ResidentTexture(RenderTextureId Texture, LinkedListNode<LumenNativeSurfaceKey> Node);
}
