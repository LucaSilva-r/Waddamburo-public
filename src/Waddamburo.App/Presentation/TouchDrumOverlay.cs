using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// Draws <see cref="TouchDrum"/>: the drum's top half at the bottom of the stage (rim and face, each half flashing
/// when hit), the menus' buttons, and the back and search buttons in the top corners.
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
    }

    /// <summary>The menu's buttons (<see cref="TouchDrum.MenuButtons"/>: up, OK, down) and the back button.</summary>
    public static IEnumerable<RenderQuad> MenuQuads(OverlayPainter painter, ulong[] hits, ulong now)
    {
        foreach (var (code, x, y, width, height) in TouchDrum.MenuButtons)
        {
            float w = width * StageWidth, h = height * StageHeight;
            yield return painter.Canvas(("touch-menu", code), x * StageWidth, y * StageHeight, w, h, canvas =>
            {
                canvas.RoundedRect(0, 0, w, h, 16, (40, 40, 40), 3, (230, 230, 230));
                (byte, byte, byte) ink = (240, 240, 240);
                if (code == TouchDrum.Ok)
                    canvas.Text("OK", w / 2, h / 2, w * .6f, h * .5f);
                else if (code == TouchDrum.Up)
                    canvas.Triangle((w / 2, h * .25f), (w * .25f, h * .72f), (w * .75f, h * .72f), ink);
                else
                    canvas.Triangle((w / 2, h * .75f), (w * .25f, h * .28f), (w * .75f, h * .28f), ink);
            }, .6f + .4f * flash(hits[code], now));
        }
    }

    /// <summary>The screen keyboard's show/hide button (<see cref="TouchDrum.KeyboardButton"/>): a keyboard of keys.</summary>
    public static RenderQuad KeyboardQuad(OverlayPainter painter, ulong[] hits, ulong now)
    {
        var (x, y, width, height) = TouchDrum.KeyboardButton;
        float w = width * StageWidth, h = height * StageHeight;
        return painter.Canvas("touch-keyboard", x * StageWidth, y * StageHeight, w, h, canvas =>
        {
            canvas.RoundedRect(0, 0, w, h, 16, (40, 40, 40), 3, (230, 230, 230));
            // Three rows of keys, the space bar under them.
            float key = w * .11f, gap = w * .04f, left = w * .17f, top = h * .26f;
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 5; column++)
                    canvas.RoundedRect(left + column * (key + gap), top + row * (key + gap), key, key, 2, (240, 240, 240), 0);
            canvas.RoundedRect(left + key + gap, top + 3 * (key + gap), 3 * key + 2 * gap, key * .8f, 2, (240, 240, 240), 0);
        }, .6f + .4f * flash(hits[TouchDrum.Keyboard], now));
    }

    /// <summary>Song select's search button (Tab): a rounded square with a magnifier, in the top-right corner.</summary>
    public static RenderQuad SearchQuad(OverlayPainter painter, ulong[] hits, ulong now)
    {
        var size = TouchDrum.SearchSize * StageHeight - 16;
        return painter.Canvas("touch-search", StageWidth - 8 - size, 8, size, size, canvas =>
        {
            canvas.RoundedRect(0, 0, size, size, 12, (40, 40, 40), 3, (230, 230, 230));
            // The lens (a ring) and its handle (a thin diagonal bar of two triangles).
            canvas.Ring(size * .2f, size * .2f, size * .42f, size * .42f, size * .21f, 5, (240, 240, 240));
            (float X, float Y) a = (size * .58f, size * .62f), b = (size * .62f, size * .58f), c = (size * .82f, size * .78f), d = (size * .78f, size * .82f);
            canvas.Triangle(a, b, c, (240, 240, 240));
            canvas.Triangle(a, c, d, (240, 240, 240));
        }, .6f + .4f * flash(hits[TouchDrum.Search], now));
    }

    /// <summary>The notices button (E): a rounded square with a bell, beside the back button.</summary>
    public static RenderQuad NoticesQuad(OverlayPainter painter, ulong[] hits, ulong now)
    {
        var size = TouchDrum.BackSize * StageHeight - 16;
        return painter.Canvas("touch-notices", TouchDrum.BackSize * StageHeight + 8, 8, size, size, canvas =>
        {
            (byte, byte, byte) ink = (240, 240, 240);
            canvas.RoundedRect(0, 0, size, size, 12, (40, 40, 40), 3, (230, 230, 230));
            // The bell: a dome, its flared rim, the clapper under it.
            canvas.RoundedRect(size * .3f, size * .22f, size * .4f, size * .46f, size * .2f, ink, 0);
            canvas.RoundedRect(size * .22f, size * .6f, size * .56f, size * .1f, size * .05f, ink, 0);
            canvas.RoundedRect(size * .44f, size * .72f, size * .12f, size * .1f, size * .05f, ink, 0);
        }, .6f + .4f * flash(hits[TouchDrum.Notices], now));
    }

    /// <summary>The back button (Escape): a rounded square with a left arrow, in the top-left corner.</summary>
    public static RenderQuad BackQuad(OverlayPainter painter, ulong[] hits, ulong now)
    {
        var size = TouchDrum.BackSize * StageHeight - 16;
        return painter.Canvas("touch-back", 8, 8, size, size, canvas =>
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
