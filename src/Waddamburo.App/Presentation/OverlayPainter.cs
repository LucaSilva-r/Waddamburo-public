using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Draws Waddamburo's own overlays on the 1280x720 stage: filled rectangles, rounded panels and text
/// (the game's outlined font), at the window's pixel scale. Texts and panels are cached by what they
/// show; one not drawn for a while is released (score lists, clocks and counters change). Text missing
/// from the cache is drawn on a worker (<paramref name="asynchronous"/>) and shows a frame or two later,
/// so menus, values and hints never stall a frame (a 4K hint line took 10-15 ms on the main thread);
/// headless runs and screenshots draw it at once. Call <see cref="EndFrame"/> once per displayed frame.
/// </summary>
internal sealed class OverlayPainter(SdlApplication application, string fontPath, bool asynchronous = true) : IDisposable
{
    public const float StageWidth = 1280, StageHeight = 720;
    // Unused this long, a texture is dropped: long enough that a menu opened now and then (the pause menu)
    // is not drawn again each time (300 frames was 1.25 s at 240 fps).
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);

    private readonly Dictionary<object, (RenderTextureId Texture, long LastUsed)> _cache = []; // Stopwatch timestamps
    private RenderTextureId? _white;
    private int _frame;
    // Texts being drawn on workers, by cache key.
    private readonly Dictionary<object, Task<VectorCanvas>> _pending = [];
    // Each slot's last text shown (its cache key).
    private readonly Dictionary<object, object> _slots = [];

    /// <summary>Texture pixels per stage pixel (1-3, from the window's height).</summary>
    public uint Scale => Scale720(application);

    public static uint Scale720(SdlApplication application) =>
        (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);

    /// <summary>A filled rectangle (stage units).</summary>
    public RenderQuad Rect(float x, float y, float width, float height, RenderColor colour)
    {
        _white ??= application.UploadRgba8(1, 1, [255, 255, 255, 255]);
        return Quad(_white.Value, x, y, width, height, colour);
    }

    /// <summary>
    /// Text fitted into a box: centred (<paramref name="anchor"/> 0.5), left-aligned at <paramref name="x"/>
    /// (0) or right-aligned to it (1); shrunk to fit. <paramref name="tint"/> recolours the fill (the outline stays).
    /// <paramref name="slot"/>: a place whose text changes (a clock); while the new text is being drawn, the
    /// slot's previous one stays instead of a gap.
    /// </summary>
    public RenderQuad Text(string text, float x, float centreY, float width, float height, float anchor = 0.5f,
        (byte R, byte G, byte B)? tint = null, float alpha = 1, object? slot = null)
    {
        var scale = Scale;
        var key = ("text", text, width, height, anchor, tint, scale);
        VectorCanvas draw()
        {
            var canvas = new VectorCanvas((int)width, (int)height, (int)scale, fontPath);
            canvas.Text(text, width * anchor, height / 2, width, height, anchorX: anchor, tint: tint);
            return canvas;
        }
        Func<VectorCanvas> source = draw;
        if (asynchronous && !_cache.ContainsKey(key))
        {
            if (!_pending.TryGetValue(key, out var drawing))
                _pending[key] = drawing = Task.Run(draw);
            if (!drawing.IsCompleted)
                return slot is not null && _slots.TryGetValue(slot, out var previous) && _cache.ContainsKey(previous)
                    ? Quad(_cache[previous].Texture, x - width * anchor, centreY - height / 2, width, height, new RenderColor(1, 1, 1, alpha))
                    : Rect(x, centreY, 0, 0, new RenderColor(0, 0, 0, 0)); // nothing yet
            _pending.Remove(key);
            if (drawing.IsCompletedSuccessfully)
                source = () => drawing.Result; // (a failed one is drawn here instead)
        }
        var texture = cached(key, source);
        if (slot is not null)
            _slots[slot] = key;
        return Quad(texture, x - width * anchor, centreY - height / 2, width, height, new RenderColor(1, 1, 1, alpha));
    }

    /// <summary>
    /// A rounded panel with an outline (stage units), as nine quads over one small drawing: the corners as
    /// drawn, the edges and middle stretched. Drawn whole, a 4K score list panel took 0.7 s and 20 MB.
    /// </summary>
    public RenderQuad[] Panel(float x, float y, float width, float height, (byte R, byte G, byte B) fill,
        (byte R, byte G, byte B) outline, float radius = 14, float outlineWidth = 3, float alpha = 1)
    {
        var scale = Scale;
        // The corner cell holds the 1-unit inset, the curve and the outline; the tile's 2-unit middle is plain fill.
        var corner = MathF.Ceiling(radius + outlineWidth) + 2;
        var tile = corner * 2 + 2;
        if (width < corner * 2 || height < corner * 2)
            corner = 0; // too small to slice: drawn whole
        var (tileWidth, tileHeight) = corner == 0 ? (width, height) : (tile, tile);
        var texture = cached(("panel", tileWidth, tileHeight, fill, outline, radius, outlineWidth, scale), () =>
        {
            var canvas = new VectorCanvas((int)tileWidth, (int)tileHeight, (int)scale, fontPath);
            canvas.RoundedRect(1, 1, tileWidth - 2, tileHeight - 2, radius, fill, outlineWidth, outline);
            return canvas;
        });
        var tint = new RenderColor(1, 1, 1, alpha);
        if (corner == 0)
            return [Quad(texture, x, y, width, height, tint)];
        // Columns and rows: (destination start, size, tile start, size), in stage units.
        (float At, float Size, float From, float Span)[] columns =
            [(x, corner, 0, corner), (x + corner, width - corner * 2, corner, tile - corner * 2), (x + width - corner, corner, tile - corner, corner)];
        (float At, float Size, float From, float Span)[] rows =
            [(y, corner, 0, corner), (y + corner, height - corner * 2, corner, tile - corner * 2), (y + height - corner, corner, tile - corner, corner)];
        return [.. rows.SelectMany(row => columns.Select(column => RenderQuad.FromRectangles(texture,
            new(column.At / StageWidth, row.At / StageHeight, column.Size / StageWidth, row.Size / StageHeight),
            new(column.From / tile, row.From / tile, column.Span / tile, row.Span / tile), tint, RenderColor.Transparent)))];
    }

    /// <summary>
    /// A picture of the caller's own (<paramref name="draw"/> on a canvas of the box's size), cached under
    /// <paramref name="key"/>: give it everything the picture shows.
    /// </summary>
    public RenderQuad Canvas(object key, float x, float y, float width, float height, Action<VectorCanvas> draw, float alpha = 1)
    {
        var scale = Scale;
        var texture = cached(("canvas", key, width, height, scale), () =>
        {
            var canvas = new VectorCanvas((int)width, (int)height, (int)scale, fontPath);
            draw(canvas);
            return canvas;
        });
        return Quad(texture, x, y, width, height, new RenderColor(1, 1, 1, alpha));
    }

    public static RenderQuad Quad(RenderTextureId texture, float x, float y, float width, float height, RenderColor? tint = null) =>
        RenderQuad.FromRectangles(texture, new(x / StageWidth, y / StageHeight, width / StageWidth, height / StageHeight),
            RenderRectangle.Full, tint ?? RenderColor.White, RenderColor.Transparent);

    /// <summary>One displayed frame is done: what was not drawn for a while is released.</summary>
    public void EndFrame()
    {
        _frame++;
        if (_frame % 60 != 0)
            return;
        // Texts drawn for a frame that has passed (a value stepped over) wait as finished canvases; they
        // join the cache's eviction once uploaded, here.
        foreach (var (key, drawing) in _pending.Where(static pair => pair.Value.IsCompleted).ToList())
        {
            _pending.Remove(key);
            if (drawing.IsCompletedSuccessfully)
                cached(key, () => drawing.Result);
        }
        foreach (var (key, entry) in _cache.Where(static pair => System.Diagnostics.Stopwatch.GetElapsedTime(pair.Value.LastUsed) > Keep).ToList())
        {
            application.ReleaseTexture(entry.Texture);
            _cache.Remove(key);
        }
    }

    private RenderTextureId cached(object key, Func<VectorCanvas> draw)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            _cache[key] = (entry.Texture, System.Diagnostics.Stopwatch.GetTimestamp());
            return entry.Texture;
        }
        var canvas = draw();
        var texture = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        _cache[key] = (texture, System.Diagnostics.Stopwatch.GetTimestamp());
        return texture;
    }

    public void Dispose()
    {
        if (_white is { } white)
            application.ReleaseTexture(white);
        foreach (var (texture, _) in _cache.Values)
            application.ReleaseTexture(texture);
        _cache.Clear();
    }
}
