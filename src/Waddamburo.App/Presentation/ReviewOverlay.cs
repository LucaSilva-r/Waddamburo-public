using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A reviewed play's bar along the bottom of the screen: how far it is (a progress line), the time,
/// the speed and whether it is paused, and the keys.
/// </summary>
internal sealed class ReviewOverlay(SdlApplication application, string fontPath) : IDisposable
{
    private const float Top = 672, Height = 48, Margin = 16;
    private RenderTextureId? _white;
    private (string Text, uint Scale, RenderTextureId Texture)? _label;

    public IEnumerable<RenderQuad> Quads(ReviewClock review)
    {
        _white ??= application.UploadRgba8(1, 1, [255, 255, 255, 255]);
        var length = review.End - review.Start;
        var done = length > TimeSpan.Zero ? (float)((review.Position - review.Start) / length) : 0;
        var text = $"{clock(review.Position)} / {clock(review.End)}   {review.Speed:0.##}x"
            + (review.Paused ? $"   {T("review.paused")}" : "") + $"      {T("review.keys")}";
        return
        [
            rect(_white.Value, 0, Top, 1280, Height, new RenderColor(0, 0, 0, 0.6f)),
            rect(_white.Value, 0, Top, 1280, 4, new RenderColor(1, 1, 1, 0.2f)),
            rect(_white.Value, 0, Top, 1280 * Math.Clamp(done, 0, 1), 4, new RenderColor(1, 0.75f, 0.2f, 1)),
            rect(label(text), Margin, Top + 10, 1280 - 2 * Margin, 30),
        ];
    }

    private static string clock(TimeSpan time) =>
        $"{(time < TimeSpan.Zero ? "-" : "")}{(int)time.Duration().TotalMinutes}:{time.Duration().Seconds:00}";

    // One texture for the current line, redrawn when it changes (about once a second while playing).
    private RenderTextureId label(string text)
    {
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 3);
        if (_label is { } current && current.Text == text && current.Scale == scale)
            return current.Texture;
        if (_label is { } old)
            application.ReleaseTexture(old.Texture);
        var width = (int)(1280 - 2 * Margin);
        var canvas = new VectorCanvas(width, 30, (int)scale, fontPath);
        canvas.Text(text, 0, 15, width, 26, anchorX: 0);
        var texture = application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        _label = (text, scale, texture);
        return texture;
    }

    private static RenderQuad rect(RenderTextureId texture, float x, float y, float width, float height, RenderColor? tint = null) =>
        RenderQuad.FromRectangles(texture, new(x / 1280, y / 720, width / 1280, height / 720),
            RenderRectangle.Full, tint ?? RenderColor.White, RenderColor.Transparent);

    public void Dispose()
    {
        if (_white is { } white)
            application.ReleaseTexture(white);
        if (_label is { } label)
            application.ReleaseTexture(label.Texture);
    }
}
