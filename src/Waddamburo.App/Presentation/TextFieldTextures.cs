using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Movies' dynamic text fields (song select's ranking names, our movies' scores and words), drawn by the native styled
/// text renderer in the field's style (<see cref="SkinText"/>: the skin's text.json and the field's own settings; the
/// game font with the field's size, alignment and colour when it has none) on first use. The least recently drawn go
/// once more than <see cref="Kept"/> are held (every score is a new text); all go when the skin's text changes.
/// </summary>
/// <remarks>ponytail: drawn on the frame that first shows it (a 7-digit bordered score: ~0.15 ms at 1x, ~1.3 ms at 3x);
/// a worker with the previous text kept on screen, as the song titles do, if a busy screen drops frames.</remarks>
internal sealed class TextFieldTextures : IDisposable
{
    private const int Kept = 256;
    private readonly SdlApplication application;
    private readonly string fontPath;
    private readonly Dictionary<int, float> _margins = [];

    public TextFieldTextures(SdlApplication application, string fontPath)
    {
        this.application = application;
        this.fontPath = fontPath;
        LumenTextSurface.Margin = margin;
    }

    // A styled field's quad grows by its style's reach (and a unit: the renderer's own pixel of room) on every side.
    private float margin(int settings)
    {
        if (_version != SkinText.Version)
        {
            _margins.Clear();
            _version = SkinText.Version;
            release();
        }
        if (!_margins.TryGetValue(settings, out var known))
            _margins[settings] = known = SkinText.Current.Resolve(LumenTextSurface.Settings(settings), 24, 'l', 0).Style.Reach is > 0 and var reach
                ? reach + 1 : 0;
        return known;
    }
    private readonly Dictionary<(LumenNativeSurfaceKey, int), (RenderTextureId Texture, long Used)> _uploaded = [];
    private long _uses;
    private int _version = SkinText.Version;
    private bool _failed;

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!LumenTextSurface.TryParse(key, out var text, out var width, out var height, out var size, out var align, out var rgb, out var settings))
            return null;
        if (_version != SkinText.Version)
        {
            _margins.Clear();
            _version = SkinText.Version;
            release();
        }
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
        var grown = settings != 0 ? margin(settings) * 2 : 0;
        uint w = (uint)Math.Max(1, (int)MathF.Ceiling(width + grown)) * (uint)scale, h = (uint)Math.Max(1, (int)MathF.Ceiling(height + grown)) * (uint)scale;
        var (style, font) = SkinText.Current.Resolve(LumenTextSurface.Settings(settings), size, align, rgb);
        RgbaTextSurface surface;
        try
        {
            surface = NativeVerticalTextRasterizer.RenderStyled(font is not null && File.Exists(font) ? font : fontPath, text, style, scale, w, h);
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!_failed)
                Console.Error.WriteLine($"Text fields not drawn: {error.Message}");
            _failed = true;
            return null;
        }
        // The renderer's output is premultiplied; textures are straight alpha.
        var pixels = surface.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] is > 0 and < 255 and var alpha)
                for (var c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / alpha);
        var texture = application.UploadRgba8(surface.Width, surface.Height, pixels);
        _uploaded[(key, scale)] = (texture, ++_uses);
        return texture;
    }

    public void Dispose()
    {
        release();
        if (LumenTextSurface.Margin == margin)
            LumenTextSurface.Margin = null;
    }

    private void release()
    {
        foreach (var (texture, _) in _uploaded.Values)
            application.ReleaseTexture(texture);
        _uploaded.Clear();
    }
}
