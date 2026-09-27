using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The cabinet's pairing pill, drawn over everything at the top of the screen: a yellow capsule
/// with a red disc (the drum's colours, after TaikoRecomp's pairing pill), the six-digit code in the
/// game's outlined title font and its countdown in the disc. After a pairing it shows the player's
/// name instead. Rebuilt only when its text changes.
/// </summary>
/// <remarks><paramref name="top"/> is the pill's top edge on the 1280x720 stage.</remarks>
internal sealed class PairingPill(SdlApplication application, string fontPath, float top = 12) : IDisposable
{
    public const int Width = 272, Height = 64, Disc = 64;
    private static readonly (byte R, byte G, byte B) Yellow = (254, 205, 1), Red = (249, 71, 40);
    private (string Text, string? Badge)? _shown;
    private (string Text, string? Badge, uint Scale)? _drawn;
    private RenderTextureId? _texture;

    /// <summary><paramref name="badge"/> goes in the red disc (the countdown); null leaves it empty.</summary>
    public void Show(string text, string? badge) => _shown = (text, badge);

    public void Hide() => _shown = null;

    public IEnumerable<RenderQuad> Quads()
    {
        if (_shown is not { } shown)
            return [];
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 4);
        if (_drawn != (shown.Text, shown.Badge, scale))
        {
            if (_texture is { } old)
                application.ReleaseTexture(old);
            _texture = application.UploadRgba8((uint)(Width * scale), (uint)(Height * scale), draw(shown.Text, shown.Badge, (int)scale));
            _drawn = (shown.Text, shown.Badge, scale);
        }
        return [RenderQuad.FromRectangles(_texture!.Value,
            new RenderRectangle((1280f - Width) / 2 / 1280f, top / 720f, Width / 1280f, Height / 720f),
            RenderRectangle.Full, RenderColor.White, RenderColor.Transparent)];
    }

    public void Dispose()
    {
        if (_texture is { } texture)
            application.ReleaseTexture(texture);
    }

    private byte[] draw(string text, string? badge, int scale)
    {
        var canvas = new VectorCanvas(Width, Height, scale, fontPath);
        float s = scale, radius = 30.5f * s, outline = 3f * s, middle = 32f * s;
        for (var y = 0; y < canvas.PixelHeight; y++)
            for (var x = 0; x < canvas.PixelWidth; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // Signed distances (negative inside): the capsule, then the disc over its left end.
                var capsule = MathF.Sqrt(MathF.Pow(px - Math.Clamp(px, 32f * s, (Width - 34f) * s), 2) + MathF.Pow(py - middle, 2)) - radius;
                var disc = MathF.Sqrt(MathF.Pow(px - middle, 2) + MathF.Pow(py - middle, 2)) - radius;
                canvas.Blend(x, y, (0, 0, 0), VectorCanvas.Coverage(capsule));
                canvas.Blend(x, y, Yellow, VectorCanvas.Coverage(capsule + outline));
                canvas.Blend(x, y, (0, 0, 0), VectorCanvas.Coverage(disc));
                canvas.Blend(x, y, Red, VectorCanvas.Coverage(disc + outline));
            }
        canvas.Text(text, Disc + (Width - Disc) / 2f, Height / 2f, Width - Disc - 24, Height - 12);
        if (badge is not null)
            canvas.Text(badge, Disc / 2f, Height / 2f, Disc - 14, Height - 12);
        return canvas.Pixels;
    }
}
