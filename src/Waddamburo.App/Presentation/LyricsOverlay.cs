using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A song's lyrics line while it plays (#45): white with an outline (dark blue, as the later arcade releases show
/// them), at the bottom centre, moved, scaled and backed as the settings say (Settings > Game Options > Lyrics); a
/// line break in it gives two lines.
/// </summary>
internal static class LyricsOverlay
{
    private const float Width = 1180, LineHeight = 40, Bottom = 700;

    public static IEnumerable<RenderQuad> Quads(OverlayPainter painter, string lyric, ArcadeSettings settings)
    {
        var scale = settings.LyricsScale / 100f;
        var (width, height) = (Width * scale, LineHeight * scale);
        var lines = lyric.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var centre = 640 + settings.LyricsX;
        var top = Bottom + settings.LyricsY - lines.Length * height;
        // ponytail: a band the whole text width, not fitted to the line's own width.
        if (settings.LyricsBackground > 0)
            yield return painter.Rect(centre - width / 2, top - height * 0.1f, width, lines.Length * height + height * 0.2f,
                new RenderColor(0, 0, 0, settings.LyricsBackground / 100f));
        var outline = (uint)settings.LyricsOutline;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            // ponytail: the game's title font (no Korean or Chinese-only glyphs); a CJK fallback font when asked.
            yield return painter.Canvas(("lyric", line, outline), centre - width / 2, top + index * height, width, height,
                canvas => canvas.Text(line, width / 2, height / 2, width, height * 0.8f, outline));
        }
    }
}
