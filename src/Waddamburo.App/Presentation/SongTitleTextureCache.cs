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

    private readonly bool _english;
    private readonly Func<bool> _squash;

    /// <remarks><paramref name="squash"/>: Song-select titles too long for their column: squashed vertically at full
    /// width (the arcade's way) instead of shrunk; read live.</remarks>
    public SongTitleTextureCache(SdlApplication application, string fontPath, bool asynchronous = true, bool english = true,
        Func<bool>? squash = null)
    {
        _squash = squash ?? (static () => false);
        _english = english;
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

    /// <summary>A folder name; <paramref name="outlineRgb"/> outlines a spine (Compact) name, as genres do.</summary>
    public LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var textureKind = kind == SongBoardTextureKind.Compact ? TitleTextureKind.Compact : TitleTextureKind.Banner;
        var outline = textureKind == TitleTextureKind.Compact ? outlineRgb : 0U;
        var key = new LumenNativeSurfaceKey($"folder-name:{name}:{textureKind}:{outline:x6}");
        _requests[key] = new TitleRequest(name, null, textureKind, outline);
        return key;
    }

    /// <summary>A picture file, as it is (8-bit RGB/RGBA PNG; anything else fails to load and draws nothing).</summary>
    public LumenNativeSurfaceKey GetImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = new LumenNativeSurfaceKey($"image:{path}");
        _requests[key] = new TitleRequest(path, null, TitleTextureKind.Image, 0);
        return key;
    }

    public LumenNativeSurfaceKey GetFolderArt(FolderArt art, FolderArtPart part)
    {
        ArgumentNullException.ThrowIfNull(art);
        var key = new LumenNativeSurfaceKey($"folder-art:{art}:{part}");
        _requests[key] = new TitleRequest(art.Name, null, TitleTextureKind.Art, 0) { Art = art, Part = part };
        return key;
    }

    public LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb)
    {
        var key = new LumenNativeSurfaceKey($"folder-description:{lines}:{outlineRgb:x6}");
        _requests[key] = new TitleRequest(lines, null, TitleTextureKind.Description, outlineRgb);
        return key;
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
            _english ? song.Descriptor.Title.English ?? song.Descriptor.Title.Primary
                : song.Descriptor.Title.Japanese ?? song.Descriptor.Title.Primary,
            _english ? song.Descriptor.EnglishSubtitle ?? song.Descriptor.Subtitle : song.Descriptor.Subtitle,
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
        var rasterKey = new RasterKey(key, rasterScale, _squash());
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
                TitleTextureKind.Gameplay or TitleTextureKind.Banner or TitleTextureKind.Image
                    or TitleTextureKind.Art or TitleTextureKind.Description => SongTitleTextProfile.GameplayTitle,
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
            if (!_asynchronous)
            {
                var surface = rasterize(request, profile, rasterScale, rasterKey.Squash);
                var texture = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
                var node = _leastRecentlyUsed.AddFirst(rasterKey);
                _resident.Add(rasterKey, new ResidentTexture(texture, node));
                evictIfNeeded();
                return texture;
            }
            if (_pending.Count < MaximumPendingRasterizations)
                _pending.Add(rasterKey, rasterizeAsync(request, profile, rasterScale, rasterKey.Squash));
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
        uint rasterScale,
        bool squash)
    {
        await _rasterizer.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => rasterize(request, profile, rasterScale, squash)).ConfigureAwait(false);
        }
        finally
        {
            _rasterizer.Release();
        }
    }

    // The feature board's header banner (the genre name tab, 256x56): the name kept to its aspect,
    // shrunk to fit.
    // ponytail: box and size guessed from the genre header art; measure the feature_board_yoko shape if off.
    private const int BannerWidth = 256, BannerHeight = 56;

    private RgbaTextSurface rasterize(TitleRequest request, SongTitleTextProfile profile, uint rasterScale, bool squash)
    {
        if (request.Kind == TitleTextureKind.Image)
        {
            // ponytail: the repo's own PNG reader (8-bit RGB/RGBA, no palette/16-bit); a real decoder when users hit it.
            var (width, height, rgba) = Waddamburo.Upscale.PngFile.Read(request.Text);
            return new RgbaTextSurface(width, height, rgba);
        }
        if (request.Kind == TitleTextureKind.Description)
            return FolderArtPainter.Description(request.Text, _fontPath, request.OutlineRgb, (int)rasterScale, squash);
        if (request.Art is { } art)
            return request.Part == FolderArtPart.Box
                ? FolderArtPainter.Box(art, _fontPath, (int)rasterScale)
                : FolderArtPainter.Pattern(art, _fontPath, (int)rasterScale);
        if (request.Kind != TitleTextureKind.Banner)
            return NativeVerticalTextRasterizer.RenderSongTitle(
                _fontPath, request.Text, request.Subtitle, profile, request.OutlineRgb, rasterScale, squash);
        var canvas = new VectorCanvas(BannerWidth, BannerHeight, (int)rasterScale, _fontPath);
        canvas.Text(request.Text, BannerWidth / 2f, BannerHeight / 2f, BannerWidth - 16, BannerHeight - 8);
        return new RgbaTextSurface((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
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
        uint OutlineRgb)
    {
        public FolderArt? Art { get; init; }
        public FolderArtPart Part { get; init; }
    }
    private enum TitleTextureKind
    {
        Compact,
        Expanded,
        Transition,
        Gameplay,
        Banner,
        Image,
        Art,
        Description,
    }
    private readonly record struct RasterKey(LumenNativeSurfaceKey Surface, uint RasterScale, bool Squash);
    private sealed record ResidentTexture(RenderTextureId Texture, LinkedListNode<RasterKey> Node);
}
