using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

internal sealed class SongTitleTextureCache : ISongBoardTextureService, IDisposable
{
    private const int MaximumResidentTextures = 64;
    private const int MaximumPendingRasterizations = 16;
    private const int MaximumUploadsPerFrame = 2;
    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly bool _asynchronous;
    private readonly Dictionary<LumenNativeSurfaceKey, TitleRequest> _requests = [];
    private readonly Dictionary<RasterKey, ResidentTexture> _resident = [];
    private readonly Dictionary<RasterKey, Task<RgbaTextSurface>> _pending = [];
    private readonly Dictionary<RasterKey, Exception> _failures = [];
    private readonly LinkedList<RasterKey> _leastRecentlyUsed = [];
    private readonly SemaphoreSlim _rasterizer = new(1, 1);
    private bool _disposed;

    public SongTitleTextureCache(SdlApplication application, string fontPath, bool asynchronous = true)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        _fontPath = Path.GetFullPath(fontPath);
        if (!File.Exists(_fontPath))
            throw new FileNotFoundException("The configured Song Select font does not exist.", _fontPath);
        _asynchronous = asynchronous;
    }

    public LumenNativeSurfaceKey GetSongTitle(
        SongSelectSong song,
        SongBoardTextureStyle style,
        SongBoardTextureKind kind)
    {
        ArgumentNullException.ThrowIfNull(song);
        var textureKind = kind switch
        {
            SongBoardTextureKind.Compact => TitleTextureKind.Compact,
            SongBoardTextureKind.Expanded => TitleTextureKind.Expanded,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var outlineRgb = kind == SongBoardTextureKind.Compact ? style.CompactOutlineRgb : 0U;
        return register(song, textureKind, outlineRgb);
    }

    public LumenNativeSurfaceKey GetTransitionTitle(SongSelectSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return register(song, TitleTextureKind.Transition, 0U);
    }

    /// <summary>Title for gameplay song_info's 720x64 song_name slot.</summary>
    public LumenNativeSurfaceKey GetGameplayTitle(SongSelectSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return register(song, TitleTextureKind.Gameplay, 0U);
    }

    private LumenNativeSurfaceKey register(
        SongSelectSong song,
        TitleTextureKind kind,
        uint outlineRgb)
    {
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

    public void UploadCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completed = _pending
            .Where(pair => pair.Value.IsCompleted)
            .Take(MaximumUploadsPerFrame)
            .ToArray();
        foreach (var (key, task) in completed)
        {
            _pending.Remove(key);
            try
            {
                var surface = task.GetAwaiter().GetResult();
                var texture = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
                var node = _leastRecentlyUsed.AddFirst(key);
                _resident.Add(key, new ResidentTexture(texture, node));
                evictIfNeeded();
            }
            catch (Exception exception)
            {
                _failures[key] = exception;
            }
        }
    }

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_requests.TryGetValue(key, out var request))
            throw new KeyNotFoundException($"No title content is registered for native surface '{key}'.");

        var pixelSize = _application.GetPixelSize();
        var rasterScale = (uint)Math.Clamp((pixelSize.Height + 719) / 720, 1, 4);
        var rasterKey = new RasterKey(key, rasterScale);
        if (_resident.TryGetValue(rasterKey, out var cached))
        {
            touch(rasterKey, cached.Node);
            return cached.Texture;
        }
        if (_failures.TryGetValue(rasterKey, out var failure))
            throw new InvalidOperationException($"Song title '{key}' could not be rasterized.", failure);
        if (!_pending.ContainsKey(rasterKey))
        {
            var profile = request.Kind switch
            {
                TitleTextureKind.Compact => SongTitleTextProfile.Compact,
                TitleTextureKind.Expanded => SongTitleTextProfile.Expanded,
                TitleTextureKind.Transition => SongTitleTextProfile.Transition,
                TitleTextureKind.Gameplay => SongTitleTextProfile.GameplayTitle,
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
            if (!_asynchronous)
            {
                var surface = NativeVerticalTextRasterizer.RenderSongTitle(
                    _fontPath,
                    request.Text,
                    request.Subtitle,
                    profile,
                    request.OutlineRgb,
                    rasterScale);
                var texture = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
                var node = _leastRecentlyUsed.AddFirst(rasterKey);
                _resident.Add(rasterKey, new ResidentTexture(texture, node));
                evictIfNeeded();
                return texture;
            }
            if (_pending.Count < MaximumPendingRasterizations)
                _pending.Add(rasterKey, rasterizeAsync(request, profile, rasterScale));
        }

        var fallback = _leastRecentlyUsed.FirstOrDefault(node => node.Surface == key);
        if (fallback == default)
            return null;
        var fallbackTexture = _resident[fallback];
        touch(fallback, fallbackTexture.Node);
        return fallbackTexture.Texture;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var texture in _resident.Values)
            _application.ReleaseTexture(texture.Texture);
        foreach (var task in _pending.Values)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _resident.Clear();
        _pending.Clear();
        _failures.Clear();
        _leastRecentlyUsed.Clear();
    }

    private async Task<RgbaTextSurface> rasterizeAsync(
        TitleRequest request,
        SongTitleTextProfile profile,
        uint rasterScale)
    {
        await _rasterizer.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => NativeVerticalTextRasterizer.RenderSongTitle(
                _fontPath,
                request.Text,
                request.Subtitle,
                profile,
                request.OutlineRgb,
                rasterScale)).ConfigureAwait(false);
        }
        finally
        {
            _rasterizer.Release();
        }
    }

    private void touch(RasterKey key, LinkedListNode<RasterKey> node)
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
        TitleTextureKind Kind,
        uint OutlineRgb);
    private enum TitleTextureKind
    {
        Compact,
        Expanded,
        Transition,
        Gameplay,
    }
    private readonly record struct RasterKey(LumenNativeSurfaceKey Surface, uint RasterScale);
    private sealed record ResidentTexture(RenderTextureId Texture, LinkedListNode<RasterKey> Node);
}
