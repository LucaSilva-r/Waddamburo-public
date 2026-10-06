using Waddamburo.Game.Lumen;
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
    private readonly Dictionary<(string, int), RenderTextureId> _uploaded = [];

    public RenderTextureId? Resolve(LumenNativeSurfaceKey key)
    {
        if (SpeedText.TryParse(key, out var speedWidth, out var speedHeight, out var speed))
            return speedLabel(speed, speedWidth, speedHeight);
        if (!NameText.TryParse(key, out var name))
            return null;
        var scale = Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_uploaded.TryGetValue((name, scale), out var texture))
            return texture;
        var (width, height) = (NameText.Box.Width, NameText.Box.Height);
        var canvas = new VectorCanvas((int)width, (int)height, scale, fontPath);
        canvas.Text(name, width / 2, height / 2, width, height);
        return _uploaded[(name, scale)] = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
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

/// <summary>
/// Gameplay's mode (a replay, practice) in song_info's top-right corner, in place of the song number it
/// alternates with the title, drawn as the title is (the same 720x64 text): over the number's <c>stage</c>
/// clip (120x48 around its origin), ending at the number's right edge.
/// </summary>
internal static class ModeLabel
{
    public static readonly LumenNativeSurfacePlacement Box = new(60 - 720, -32, 720, 64);

    /// <summary>Shows <paramref name="text"/> (a <see cref="SongTitleTextureCache.GetGameplayText"/> surface) in place of the song number (null: the number again).</summary>
    public static void Show(Waddamburo.Lumen.Runtime.LumenPlayer songInfo, LumenNativeSurfaceKey? text) =>
        songInfo.SetInstanceOverlay("main/stage", text, Box, replace: true);
}
