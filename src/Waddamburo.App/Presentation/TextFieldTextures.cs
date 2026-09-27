using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Movies' dynamic text fields (e.g. song select's ranking names), drawn in the game font on first use.
/// ponytail: the title rasterizer's outlined text with only its white fill kept, in the field's colour
/// (no horizontal plain-text mode in the native rasterizer yet). Never evicted.
/// </summary>
internal sealed class TextFieldTextures(SdlApplication application, string fontPath) : IDisposable
{
    private readonly Dictionary<(LumenNativeSurfaceKey, int), RenderTextureId> _uploaded = [];

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!LumenTextSurface.TryParse(key, out var text, out var width, out var height, out var size, out var align, out var rgb))
            return null;
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_uploaded.TryGetValue((key, scale), out var texture))
            return texture;
        int w = Math.Max(1, (int)MathF.Ceiling(width)), h = Math.Max(1, (int)MathF.Ceiling(height));
        var canvas = new VectorCanvas(w, h, scale, fontPath);
        var anchor = align switch { 'r' => 1f, 'c' => 0.5f, _ => 0f };
        canvas.Text(text, anchor * w, h / 2f, w, Math.Min(h, size), anchorX: anchor,
            tint: ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return _uploaded[(key, scale)] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    public void Dispose()
    {
        foreach (var texture in _uploaded.Values)
            application.ReleaseTexture(texture);
        _uploaded.Clear();
    }
}
