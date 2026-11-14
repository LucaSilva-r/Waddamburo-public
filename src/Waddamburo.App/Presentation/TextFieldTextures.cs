using SkiaSharp;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Movies' dynamic text fields (song select's ranking names, our movies' scores and words), drawn in the game font
/// with SkiaSharp on first use: a score that changes at every hit costs a fraction of a millisecond (the outlined
/// title rasterizer took 3-4 ms a string, a dropped frame at each hit). The least recently drawn go once more than
/// <see cref="Kept"/> are held (every score is a new text).
/// </summary>
internal sealed class TextFieldTextures(SdlApplication application, string fontPath) : IDisposable
{
    private const int Kept = 256;
    private readonly Dictionary<(LumenNativeSurfaceKey, int), (RenderTextureId Texture, long Used)> _uploaded = [];
    private long _uses;
    private SKTypeface? _typeface;

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!LumenTextSurface.TryParse(key, out var text, out var width, out var height, out var size, out var align, out var rgb))
            return null;
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_uploaded.TryGetValue((key, scale), out var kept))
        {
            _uploaded[(key, scale)] = kept with { Used = ++_uses };
            return kept.Texture;
        }
        if (_uploaded.Count >= Kept)
        {
            var oldest = _uploaded.MinBy(static pair => pair.Value.Used);
            application.ReleaseTexture(oldest.Value.Texture);
            _uploaded.Remove(oldest.Key);
        }
        int w = Math.Max(1, (int)MathF.Ceiling(width)) * scale, h = Math.Max(1, (int)MathF.Ceiling(height)) * scale;
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _typeface ??= SKTypeface.FromFile(fontPath) ?? SKTypeface.Default;
                using var font = new SKFont(_typeface, size * scale);
                using var paint = new SKPaint { Color = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), IsAntialias = true };
                // Shrunk as a whole to fit the field's width, as the game squeezes a long name.
                var advance = font.MeasureText(text);
                if (advance > w) font.Size *= w / advance;
                advance = font.MeasureText(text, out var ink);
                var anchor = align switch { 'r' => 1f, 'c' => 0.5f, _ => 0f };
                // Flash sets text 2 px below the box top with the font's leading above the capitals, so its ink sits
                // about 0.1 em below the box centre (player_name's title: box -2..22, size 20, ink ~3..21).
                var baseline = (height / 2f + 0.1f * size) * scale - (ink.Top + ink.Bottom) / 2;
                canvas.DrawText(text, anchor * (w - advance), baseline, SKTextAlign.Left, font, paint);
            }
        }
        var texture = application.UploadRgba8((uint)w, (uint)h, bitmap.Bytes);
        _uploaded[(key, scale)] = (texture, ++_uses);
        return texture;
    }

    public void Dispose()
    {
        foreach (var (texture, _) in _uploaded.Values)
            application.ReleaseTexture(texture);
        _uploaded.Clear();
        _typeface?.Dispose();
    }
}
