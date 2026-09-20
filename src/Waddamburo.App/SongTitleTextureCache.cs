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

    public LumenNativeSurfaceKey GetSongTitle(
        SongSelectSong song,
        SongBoardTextureStyle style,
        SongBoardTextureKind kind)
    {
        ArgumentNullException.ThrowIfNull(song);
        var outlineRgb = kind == SongBoardTextureKind.Compact ? style.CompactOutlineRgb : 0U;
        var key = new LumenNativeSurfaceKey(
            $"song-title:{song.Descriptor.Key}:{kind}:{outlineRgb:x6}");
        var request = new TitleRequest(
            song.Descriptor.Title.Primary,
            song.Descriptor.Subtitle,
            kind,
            outlineRgb);
        if (_requests.TryGetValue(key, out var existing) && existing != request)
            throw new InvalidOperationException($"Native surface key '{key}' was assigned conflicting content.");
        _requests[key] = request;
        return key;
    }

    public RenderTextureId Resolve(LumenNativeSurfaceKey key)
    {
        if (!_requests.TryGetValue(key, out var request))
            throw new KeyNotFoundException($"No title content is registered for native surface '{key}'.");

        var pixelSize = _application.GetPixelSize();
        var rasterScale = (uint)Math.Clamp((pixelSize.Height + 719) / 720, 1, 4);
        if (_resident.TryGetValue(key, out var stale) && stale.RasterScale != rasterScale)
        {
            _leastRecentlyUsed.Remove(stale.Node);
            _resident.Remove(key);
            _application.ReleaseTexture(stale.Texture);
        }
        else if (_resident.TryGetValue(key, out var cached))
        {
            touch(key, cached.Node);
            return cached.Texture;
        }

        var profile = request.Kind switch
        {
            SongBoardTextureKind.Compact => SongTitleTextProfile.Compact,
            SongBoardTextureKind.Expanded => SongTitleTextProfile.Expanded,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        var surface = NativeVerticalTextRasterizer.RenderSongTitle(
            _fontPath,
            request.Text,
            request.Subtitle,
            profile,
            request.OutlineRgb,
            rasterScale);
        var texture = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
        var node = _leastRecentlyUsed.AddFirst(key);
        _resident.Add(key, new ResidentTexture(texture, rasterScale, node));
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

    private sealed record TitleRequest(
        string Text,
        string? Subtitle,
        SongBoardTextureKind Kind,
        uint OutlineRgb);
    private sealed record ResidentTexture(
        RenderTextureId Texture,
        uint RasterScale,
        LinkedListNode<LumenNativeSurfaceKey> Node);
}
