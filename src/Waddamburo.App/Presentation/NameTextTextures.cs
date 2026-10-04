using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Name boards' names (<see cref="NameText"/>): white in a black outline, letters spaced out like the
/// board's own lettering, as the entry overlay draws them. Never evicted (a handful of names per session).
/// </summary>
internal sealed class NameTextTextures(SdlApplication application, string fontPath) : IDisposable
{
    private readonly Dictionary<(string, int), RenderTextureId> _uploaded = [];

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (!NameText.TryParse(key, out var name))
            return null;
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_uploaded.TryGetValue((name, scale), out var texture))
            return texture;
        var (width, height) = (NameText.Box.Width, NameText.Box.Height);
        var canvas = new VectorCanvas((int)width, (int)height, scale, fontPath);
        canvas.Text(name, width / 2, height / 2, width, height, spacing: 0.06f);
        return _uploaded[(name, scale)] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
    }

    public void Dispose()
    {
        foreach (var texture in _uploaded.Values)
            application.ReleaseTexture(texture);
        _uploaded.Clear();
    }
}
