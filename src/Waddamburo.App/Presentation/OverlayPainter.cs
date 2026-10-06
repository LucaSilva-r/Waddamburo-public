using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Draws Waddamburo's own overlays on the 1280x720 stage: filled rectangles, rounded panels and text
/// (the game's outlined font), at the window's pixel scale. Texts and panels are cached by what they
/// show; one not drawn for a few seconds is released (score lists, clocks and counters change).
/// Call <see cref="EndFrame"/> once per displayed frame.
/// </summary>
internal sealed class OverlayPainter(SdlApplication application, string fontPath) : IDisposable
{
    public const float StageWidth = 1280, StageHeight = 720;
    private const int KeepFrames = 300;

    private readonly Dictionary<object, (RenderTextureId Texture, int LastFrame)> _cache = [];
    private RenderTextureId? _white;
    private int _frame;

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
    /// </summary>
    public RenderQuad Text(string text, float x, float centreY, float width, float height, float anchor = 0.5f,
        (byte R, byte G, byte B)? tint = null, float alpha = 1)
    {
        var scale = Scale;
        var texture = cached(("text", text, width, height, anchor, tint, scale), () =>
        {
            var canvas = new VectorCanvas((int)width, (int)height, (int)scale, fontPath);
            canvas.Text(text, width * anchor, height / 2, width, height, anchorX: anchor, tint: tint);
            return canvas;
        });
        return Quad(texture, x - width * anchor, centreY - height / 2, width, height, new RenderColor(1, 1, 1, alpha));
    }

    /// <summary>A rounded panel with an outline (stage units).</summary>
    public RenderQuad Panel(float x, float y, float width, float height, (byte R, byte G, byte B) fill,
        (byte R, byte G, byte B) outline, float radius = 14, float outlineWidth = 3, float alpha = 1)
    {
        var scale = Scale;
        var texture = cached(("panel", width, height, fill, outline, radius, outlineWidth, scale), () =>
        {
            var canvas = new VectorCanvas((int)width, (int)height, (int)scale, fontPath);
            canvas.RoundedRect(1, 1, width - 2, height - 2, radius, fill, outlineWidth, outline);
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
        foreach (var (key, entry) in _cache.Where(pair => _frame - pair.Value.LastFrame > KeepFrames).ToList())
        {
            application.ReleaseTexture(entry.Texture);
            _cache.Remove(key);
        }
    }

    private RenderTextureId cached(object key, Func<VectorCanvas> draw)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            _cache[key] = (entry.Texture, _frame);
            return entry.Texture;
        }
        var canvas = draw();
        var texture = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        _cache[key] = (texture, _frame);
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
