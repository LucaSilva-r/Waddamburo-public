using Waddamburo.Platform.Sdl.Text;
using Waddamburo.Skins.Green;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Name boards' names (<see cref="NameText"/>): white in a black outline, as the entry overlay
/// draws them. Never evicted (a handful of names per session).
/// </summary>
internal sealed class NameTextTextures(SdlApplication application, string fontPath) : IDisposable
{
    private const float NameSpacing = 0.07f; // em; matched by eye to the game's boards (やばいわよ)
    private readonly Dictionary<(string, int), RenderTextureId> _uploaded = [];

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (SpeedText.TryParse(key, out var speedWidth, out var speedHeight, out var speed))
            return speedLabel(speed, speedWidth, speedHeight);
        if (!NameText.TryParse(key, out var name, out var ranked))
            return null;
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_uploaded.TryGetValue((key.Value, scale), out var texture))
            return texture;
        // Ranked names use less horizontal padding before being fitted into the plate, keeping
        // the lettering close to its natural width while preserving the gap and rounded-edge inset.
        var (width, height) = (ranked ? 144 : NameText.Box.Width, NameText.Box.Height);
        var canvas = new VectorCanvas((int)width, (int)height, scale, fontPath);
        // The game's boards space their kana out: short names get up to NameSpacing between characters
        // (Latin and Japanese alike), closing up as a name fills the plate before it shrinks.
        canvas.Text(name, width / 2, height / 2, width, height, spacing: NameSpacing);
        return _uploaded[(key.Value, scale)] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    // A play speed on the option board, lettered as the names are.
    private RenderTextureId speedLabel(string text, int width, int height)
    {
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        var key = ($"{SpeedText.Prefix}{width}x{height}:{text}", scale);
        if (_uploaded.TryGetValue(key, out var texture))
            return texture;
        var canvas = new VectorCanvas(Math.Max(1, width), Math.Max(1, height), scale, fontPath);
        canvas.Text(text, width / 2f, height / 2f, width, height);
        return _uploaded[key] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    public void Dispose()
    {
        foreach (var texture in _uploaded.Values)
            application.ReleaseTexture(texture);
        _uploaded.Clear();
    }
}
