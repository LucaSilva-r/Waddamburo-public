using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Draws <see cref="TouchDrum"/>: the drum's top half at the bottom of the stage (rim and face, each half flashing
/// when hit) and the back button in the top-left corner.
/// </summary>
internal static class TouchDrumOverlay
{
    private const float StageWidth = 1280, StageHeight = 720;
    private static readonly (byte, byte, byte) Rim = (66, 40, 30), Face = (236, 214, 170), Flash = (255, 255, 255);
    private const double FlashSeconds = 0.15;

    public static IEnumerable<RenderQuad> Quads(OverlayPainter painter, ulong[] hits, ulong now)
    {
        var centreX = TouchDrum.CenterX * StageWidth;
        var centreY = TouchDrum.CenterY * StageHeight;
        var rim = TouchDrum.RimRadius * StageHeight;
        var face = TouchDrum.FaceRadius * StageHeight;
        foreach (var right in (bool[])[false, true])
        {
            var (ka, don) = right ? (3, 2) : (0, 1);
            yield return quarter(painter, "rim", right, centreX, centreY, rim, Rim, .55f);
            yield return quarter(painter, "face", right, centreX, centreY, face, Face, .45f);
            if (flash(hits[ka], now) is > 0 and var kaFlash)
                yield return quarter(painter, "flash-rim", right, centreX, centreY, rim, Flash, .5f * kaFlash);
            if (flash(hits[don], now) is > 0 and var donFlash)
                yield return quarter(painter, "flash-face", right, centreX, centreY, face, Flash, .6f * donFlash);
        }
        // Back: a rounded square with a left arrow.
        var size = TouchDrum.BackSize * StageHeight - 16;
        yield return painter.Canvas("touch-back", 8, 8, size, size, canvas =>
        {
            canvas.RoundedRect(0, 0, size, size, 12, (40, 40, 40), 3, (230, 230, 230));
            canvas.Triangle((size * .28f, size / 2), (size * .62f, size * .26f), (size * .62f, size * .74f), (240, 240, 240));
        }, .6f + .4f * flash(hits[TouchDrum.Back], now));
    }

    // One side's quarter disc above the centre (the lower half lies below the stage).
    private static RenderQuad quarter(OverlayPainter painter, string kind, bool right, float centreX, float centreY,
        float radius, (byte, byte, byte) colour, float alpha) =>
        painter.Canvas(("touch-drum", kind, right), right ? centreX : centreX - radius, centreY - radius, radius, radius,
            canvas => canvas.RoundedRect(right ? -radius : 0, 0, radius * 2, radius * 2, radius, colour, 0), alpha);

    // 1 just hit, fading to 0 over FlashSeconds.
    private static float flash(ulong hit, ulong now) =>
        hit == 0 ? 0 : (float)Math.Max(0, 1 - (now - hit) / 1e9 / FlashSeconds);
}
