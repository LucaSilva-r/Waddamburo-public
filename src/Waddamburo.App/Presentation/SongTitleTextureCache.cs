using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;
using Waddamburo.MenuLayouts;

namespace Waddamburo.App.Presentation;

internal sealed class SongTitleTextureCache : ISongBoardTextureService, IDisposable
{
    internal static readonly LumenNativeSurfaceKey MetallicGoldKey = new("folder-metallic-gold");
    private RenderTextureId? _metallicGold;
    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId> _folderPegs = [];
    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId> _vectorPaints = [];
    private const int MaximumResidentTextures = 64;
    private const int MaximumPendingRasterizations = 16;
    private const int MaximumUploadsPerFrame = 2;
    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly bool _asynchronous;
    private readonly Dictionary<LumenNativeSurfaceKey, TitleRequest> _requests = [];
    private readonly Dictionary<LumenNativeSurfaceKey, LumenTextLayout> _horizontalLayouts = [];
    private readonly Dictionary<LumenNativeSurfaceKey, float> _horizontalExtents = [], _horizontalTextWidths = [];
    private readonly Dictionary<RasterKey, ResidentTexture> _resident = [];
    private readonly Dictionary<RasterKey, Task<RgbaTextSurface>> _pending = [];
    private readonly Dictionary<RasterKey, Exception> _failures = [];
    private readonly LinkedList<RasterKey> _leastRecentlyUsed = [];
    private readonly SemaphoreSlim _rasterizer = new(1, 1);
    private bool _disposed;

    private readonly Lazy<RgbaTextSurface?> _normalModeBox;
    private readonly bool _english;
    private readonly bool _gameplayTitleLeft;
    private readonly Func<bool> _squash;

    /// <remarks><paramref name="squash"/>: Song-select titles too long for their column: squashed vertically at full
    /// width (the arcade's way) instead of shrunk; read live.</remarks>
    public SongTitleTextureCache(SdlApplication application, string fontPath, bool asynchronous = true, bool english = true,
        Func<bool>? squash = null, bool gameplayTitleLeft = false, string? nativeAssetRoot = null)
    {
        _normalModeBox = new(() => nativeAssetRoot is null ? null : FolderArtPainter.NormalModeBox(nativeAssetRoot));
        _gameplayTitleLeft = gameplayTitleLeft;
        _squash = squash ?? (static () => false);
        _english = english;
        _application = application ?? throw new ArgumentNullException(nameof(application));
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        _fontPath = Path.GetFullPath(fontPath);
        if (!File.Exists(_fontPath))
            throw new FileNotFoundException("The configured Song Select font does not exist.", _fontPath);
        _asynchronous = asynchronous;
    }

    private Task? _folderGlyphPreload;

    /// <summary>Starts the small initial glyph set while the scene's archives are loading.</summary>
    public void PreloadFolderGlyphs(IEnumerable<string> names)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_folderGlyphPreload is not null) return;
        // Bound background work even when a custom catalog has many folders.
        // Only the thread-safe CPU glyph cache is touched; texture/cache requests
        // and GPU uploads remain on the main thread.
        var labels = names.Take(64).Append("Return").Distinct(StringComparer.Ordinal).ToArray();
        var font = _fontPath;
        _folderGlyphPreload = Task.Run(() =>
        {
            foreach (var label in labels)
            {
                GlyphLineRasterizer.Measure(font, label, 1);
                GlyphLineRasterizer.Measure(font, label, 2);
            }
        });
        _ = _folderGlyphPreload.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            SongBoardTextureKind.HorizontalCompact => TitleTextureKind.HorizontalRow,
            SongBoardTextureKind.HorizontalSelected => TitleTextureKind.HorizontalRow,
            SongBoardTextureKind.HorizontalExpanded => TitleTextureKind.HorizontalExpanded,
            SongBoardTextureKind.HorizontalCourse => TitleTextureKind.CourseTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var outlineRgb = kind is SongBoardTextureKind.Compact or SongBoardTextureKind.HorizontalCompact ? style.CompactOutlineRgb : 0U;
        return register(song, textureKind, outlineRgb);
    }

    /// <remarks><paramref name="subtitle"/>: shown in place of the song's own (a replay's player, practice).</remarks>
    public LumenNativeSurfaceKey GetTransitionTitle(SongSelectSong song, string? subtitle = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        return register(song, TitleTextureKind.Transition, 0U, subtitle);
    }

    /// <summary>A folder name; <paramref name="outlineRgb"/> outlines a spine (Compact) name, as genres do.</summary>
    public LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Category captions are fitted to the tab using 56-pixel glyph metrics.
        // The taller song-row font belongs only to songs and the Return row.
        var textureKind = kind switch { SongBoardTextureKind.Compact => TitleTextureKind.Compact,
            SongBoardTextureKind.HorizontalCompact or SongBoardTextureKind.HorizontalSelected => TitleTextureKind.HorizontalCompact, _ => TitleTextureKind.Banner };
        if (name == "Return" && kind is SongBoardTextureKind.HorizontalCompact or SongBoardTextureKind.HorizontalSelected) textureKind = TitleTextureKind.HorizontalRow;
        var outline = textureKind is TitleTextureKind.Compact or TitleTextureKind.HorizontalCompact ? outlineRgb : 0U;
        var centered = kind == SongBoardTextureKind.HorizontalSelected || name == "Return";
        name = SongSelectText.Name(name, Strings.Language == "en");
        var key = new LumenNativeSurfaceKey($"folder-name:{name}:{kind}:{outline:x6}");
        _requests[key] = new TitleRequest(name, null, textureKind, outline) { Centered = centered };
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
        var englishParagraph = Strings.Language == "en";
        lines = SongSelectText.Description(lines, englishParagraph);
        var key = new LumenNativeSurfaceKey($"folder-description:{(englishParagraph ? "en" : "tate")}:{lines}:{outlineRgb:x6}");
        _requests[key] = new TitleRequest(lines, null, TitleTextureKind.Description, outlineRgb)
        { EnglishParagraph = englishParagraph };
        return key;
    }

    public LumenNativeSurfaceKey GetHorizontalFolderDescription(string lines, uint outlineRgb)
    {
        lines = SongSelectText.Description(lines, Strings.Language == "en");
        var key = new LumenNativeSurfaceKey($"folder-description-horizontal:{lines}:{outlineRgb:x6}");
        _requests[key] = new TitleRequest(lines, null, TitleTextureKind.HorizontalDescription, outlineRgb);
        return key;
    }

    /// <summary>Title for gameplay song_info's 720x64 song_name slot.</summary>
    public LumenNativeSurfaceKey GetGameplayTitle(SongSelectSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return register(song, TitleTextureKind.Gameplay, 0U);
    }

    /// <summary>Any text drawn as gameplay song_info's title is (a replay's or practice's mode label).</summary>
    public LumenNativeSurfaceKey GetGameplayText(string text)
    {
        var key = new LumenNativeSurfaceKey($"gameplay-text:{text}");
        _requests[key] = new TitleRequest(text, null, TitleTextureKind.Gameplay, 0U);
        return key;
    }

    private LumenNativeSurfaceKey register(
        SongSelectSong song,
        TitleTextureKind kind,
        uint outlineRgb,
        string? subtitle = null)
    {
        var key = new LumenNativeSurfaceKey(
            $"song-title:{song.Descriptor.Key}:{kind}:{outlineRgb:x6}{(subtitle is null ? "" : ":" + subtitle)}");
        var request = new TitleRequest(
            _english ? song.Descriptor.Title.English ?? song.Descriptor.Title.Primary
                : song.Descriptor.Title.Japanese ?? song.Descriptor.Title.Primary,
            subtitle ?? (_english ? song.Descriptor.EnglishSubtitle ?? song.Descriptor.Subtitle : song.Descriptor.Subtitle),
            kind,
            outlineRgb) { Centered = kind is TitleTextureKind.HorizontalCompact or TitleTextureKind.HorizontalExpanded or TitleTextureKind.HorizontalRow };
        if (_requests.TryGetValue(key, out var existing) && existing != request)
            throw new InvalidOperationException($"Native surface key '{key}' was assigned conflicting content.");
        _requests[key] = request;
        return key;
    }

    public float GetHorizontalTextWidth(LumenNativeSurfaceKey surface)
    {
        if (_horizontalTextWidths.TryGetValue(surface, out var width)) return width;
        return !_requests.TryGetValue(surface, out var request) ? 400 : _horizontalTextWidths[surface] =
            HorizontalSongTitleRasterizer.ContentWidth(_fontPath, request.Text, (int)GetHorizontalExtent(surface), 56,
                HorizontalSongTitleRasterizer.VerticalBoardAspect);
    }

    public LumenTextLayout? GetHorizontalLayout(LumenNativeSurfaceKey surface)
    {
        if (!surface.Value.StartsWith("song-title:", StringComparison.Ordinal)
            || !_requests.TryGetValue(surface, out var request)
            || request.Kind is not (TitleTextureKind.HorizontalRow or TitleTextureKind.HorizontalExpanded or TitleTextureKind.CourseTitle)) return null;
        if (_horizontalLayouts.TryGetValue(surface, out var cached)) return cached;
        if (request.Kind == TitleTextureKind.CourseTitle)
        {
            var extent = (int)GetHorizontalExtent(surface);
            var hasSubtitle = !string.IsNullOrEmpty(request.Subtitle);
            var courseLines = new List<LumenTextLine>
            {
                new(HorizontalSongTitleRasterizer.ContentWidth(_fontPath, request.Text, extent, DifficultySelectLayout.TitleLineHeight), hasSubtitle ? 2 : 0, DifficultySelectLayout.TitleLineHeight),
            };
            if (hasSubtitle) courseLines.Add(new(HorizontalSongTitleRasterizer.ContentWidth(_fontPath, request.Subtitle!, extent, 28), DifficultySelectLayout.TitleLineHeight + 2, 28));
            return _horizontalLayouts[surface] = new(extent, DifficultySelectLayout.TitleHeight, courseLines);
        }
        var expanded = request.Kind == TitleTextureKind.HorizontalExpanded;
        var width = (int)GetHorizontalExtent(surface);
        var subtitle = expanded && !string.IsNullOrEmpty(request.Subtitle);
        var lines = new List<LumenTextLine> { new(measure(request.Text, 64), subtitle ? 2 : 0, 64) };
        if (subtitle) lines.Add(new(measure(request.Subtitle!, 32), 66, 32));
        return _horizontalLayouts[surface] = new(width, expanded ? 100 : 64, lines);

        float measure(string text, int height) => HorizontalSongTitleRasterizer.ContentWidth(_fontPath,
            text, width, height, HorizontalSongTitleRasterizer.VerticalBoardAspect);
    }

    public (float Width, float Height, FolderArtStyle Style)? GetPatternSize(LumenNativeSurfaceKey surface)
    {
        if (!_requests.TryGetValue(surface, out var request) || request.Art is not { } art
            || request.Part != FolderArtPart.Pattern) return null;
        var pattern = FolderArtPainter.Pattern(art);
        // The supplied patterns are authored at twice the game's logical resolution.
        return (pattern.Height / 2f, pattern.Width / 2f, art.Style);
    }

    public float GetHorizontalExtent(LumenNativeSurfaceKey surface)
    {
        if (_horizontalExtents.TryGetValue(surface, out var extent)) return extent;
        if (_requests.TryGetValue(surface, out var course) && course.Kind == TitleTextureKind.CourseTitle)
            return _horizontalExtents[surface] = Math.Max((int)(DifficultySelectLayout.TitleRight - DifficultySelectLayout.TitleLeft), HorizontalSongTitleRasterizer.ScrollingWidth(_fontPath, course.Text, course.Subtitle, DifficultySelectLayout.TitleHeight, titleLineHeight: DifficultySelectLayout.TitleLineHeight));
        if (!_requests.TryGetValue(surface, out var request)
            || request.Kind is not (TitleTextureKind.HorizontalCompact or TitleTextureKind.HorizontalExpanded or TitleTextureKind.HorizontalRow)) return 400;
        var expanded = request.Kind == TitleTextureKind.HorizontalExpanded;
        return _horizontalExtents[surface] = HorizontalSongTitleRasterizer.ScrollingWidth(_fontPath, request.Text,
            expanded ? request.Subtitle : null, expanded ? 100 : request.Kind == TitleTextureKind.HorizontalRow ? 64 : 56, HorizontalSongTitleRasterizer.VerticalBoardAspect, expanded ? 64 : null);
    }

    internal bool HasPendingRasterizations => _pending.Count != 0;

    /// <summary>Finishes the currently queued CPU work during loading, before animation starts.</summary>
    internal void CompletePendingForStartup()
    {
        foreach (var (key, task) in _pending.ToArray())
            try { _ = task.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                _failures[key] = exception;
                _pending.Remove(key);
            }
        // All remaining tasks are complete. Normal rendering retains its
        // two-upload budget; loading drains these bounded batches before showing.
        while (_pending.Count > 0) UploadCompleted();
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

    public bool PrepareSurface(LumenNativeSurfaceKey surface) => Resolve(surface) is not null;

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (key.Value.StartsWith("vector-ui:", StringComparison.Ordinal))
        {
            if (!_vectorPaints.TryGetValue(key, out var paint))
            {
                var surface = VectorUiPaint.Resolve(key);
                _vectorPaints[key] = paint = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
            }
            return paint;
        }
        if (key.Value.StartsWith("folder-peg:", StringComparison.Ordinal))
        {
            if (!_folderPegs.TryGetValue(key, out var peg))
            {
                var surface = FolderArtPainter.Peg(key.Value);
                if (Environment.GetEnvironmentVariable("WADDAMBURO_BROWSER_GEOMETRY_TRACE") == "1")
                    Console.WriteLine($"[peg-upload] {key.Value} size={surface.Width}x{surface.Height}");
                _folderPegs[key] = peg = _application.UploadRgba8(surface.Width, surface.Height, surface.Pixels);
            }
            return peg;
        }
        if (key == MetallicGoldKey)
        {
            if (_metallicGold is null)
            {
                // One tiny shared texture, uploaded once; the full reflection is
                // mapped to both open and closed Favourites, with no repeated tiles.
                (float At, byte R, byte G, byte B)[] stops =
                    [(0, 194, 132, 18), (.22f, 242, 196, 57), (.44f, 255, 238, 149),
                     (.50f, 255, 247, 188), (.58f, 239, 199, 63), (.78f, 207, 154, 27), (1, 245, 207, 89)];
                var pixels = new byte[256 * 4];
                var segment = 0;
                for (var y = 0; y < 256; y++)
                {
                    var position = y / 255f;
                    while (segment < stops.Length - 2 && position > stops[segment + 1].At) segment++;
                    var a = stops[segment]; var b = stops[segment + 1];
                    var t = (position - a.At) / (b.At - a.At);
                    pixels[y * 4] = (byte)MathF.Round(a.R + (b.R - a.R) * t);
                    pixels[y * 4 + 1] = (byte)MathF.Round(a.G + (b.G - a.G) * t);
                    pixels[y * 4 + 2] = (byte)MathF.Round(a.B + (b.B - a.B) * t);
                    pixels[y * 4 + 3] = 255;
                }
                _metallicGold = _application.UploadRgba8(1, 256, pixels);
            }
            return _metallicGold;
        }
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
                    or TitleTextureKind.Art or TitleTextureKind.Description or TitleTextureKind.HorizontalDescription or TitleTextureKind.CourseTitle
                    or TitleTextureKind.HorizontalCompact or TitleTextureKind.HorizontalExpanded or TitleTextureKind.HorizontalRow => SongTitleTextProfile.GameplayTitle,
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
        if (_metallicGold is { } gold) _application.ReleaseTexture(gold);
        foreach (var peg in _folderPegs.Values) _application.ReleaseTexture(peg);
        _folderPegs.Clear();
        foreach (var paint in _vectorPaints.Values) _application.ReleaseTexture(paint);
        _vectorPaints.Clear();
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
        if (request.Kind == TitleTextureKind.CourseTitle)
        {
            var width = Math.Max((int)(DifficultySelectLayout.TitleRight - DifficultySelectLayout.TitleLeft), HorizontalSongTitleRasterizer.ScrollingWidth(_fontPath, request.Text,
                request.Subtitle, DifficultySelectLayout.TitleHeight, titleLineHeight: DifficultySelectLayout.TitleLineHeight));
            return HorizontalSongTitleRasterizer.Render(_fontPath, request.Text, request.Subtitle, width,
                DifficultySelectLayout.TitleHeight, request.OutlineRgb, rasterScale, centered: true, titleLineHeight: DifficultySelectLayout.TitleLineHeight);
        }
        if (request.Kind == TitleTextureKind.HorizontalDescription)
            return HorizontalSongTitleRasterizer.RotateCounterClockwise(HorizontalTextParagraph.Render(
                _fontPath, request.Text, 400, 160, request.OutlineRgb, rasterScale));
        if (request.Kind is TitleTextureKind.HorizontalCompact or TitleTextureKind.HorizontalExpanded or TitleTextureKind.HorizontalRow)
        {
            var expanded = request.Kind == TitleTextureKind.HorizontalExpanded;
            var subtitle = expanded ? request.Subtitle : null;
            var width = HorizontalSongTitleRasterizer.ScrollingWidth(_fontPath, request.Text, subtitle, expanded ? 100 : request.Kind == TitleTextureKind.HorizontalRow ? 64 : 56,
                HorizontalSongTitleRasterizer.VerticalBoardAspect, expanded ? 64 : null);
            return HorizontalSongTitleRasterizer.RotateCounterClockwise(HorizontalSongTitleRasterizer.Render(
                _fontPath, request.Text, subtitle, width, expanded ? 100 : request.Kind == TitleTextureKind.HorizontalRow ? 64 : 56,
                request.OutlineRgb, rasterScale, HorizontalSongTitleRasterizer.VerticalBoardAspect, request.Centered, expanded ? 64 : null));
        }
        if (request.Kind == TitleTextureKind.Image)
        {
            // ponytail: the repo's own PNG reader (8-bit RGB/RGBA, no palette/16-bit); a real decoder when users hit it.
            var (width, height, rgba) = Waddamburo.Upscale.PngFile.Read(request.Text);
            return new RgbaTextSurface(width, height, rgba);
        }
        if (request.Kind == TitleTextureKind.Description)
            return FolderArtPainter.Description(request.Text, _fontPath, request.OutlineRgb, (int)rasterScale, squash,
                request.EnglishParagraph);
        if (request.Art is { } art)
            return request.Part == FolderArtPart.Box
                ? (art.Style is FolderArtStyle.Original or FolderArtStyle.Nijiiro ? _normalModeBox.Value : null) ?? FolderArtPainter.Box(art)
                : FolderArtPainter.Pattern(art);
        if (request.Kind != TitleTextureKind.Banner)
        {
            var surface = NativeVerticalTextRasterizer.RenderSongTitle(
                _fontPath, request.Text, request.Subtitle, profile, request.OutlineRgb, rasterScale, squash);
            return _gameplayTitleLeft && request.Kind == TitleTextureKind.Gameplay ? surface.AlignLeft() : surface;
        }
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
        public bool Centered { get; init; }
        public bool EnglishParagraph { get; init; }
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
        HorizontalDescription,
        HorizontalCompact,
        HorizontalRow,
        HorizontalExpanded,
        CourseTitle,
    }
    private readonly record struct RasterKey(LumenNativeSurfaceKey Surface, uint RasterScale, bool Squash);
    private sealed record ResidentTexture(RenderTextureId Texture, LinkedListNode<RasterKey> Node);
}
