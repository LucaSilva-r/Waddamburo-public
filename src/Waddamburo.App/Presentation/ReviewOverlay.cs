using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A reviewed play's bar along the bottom of the screen: how far it is (a progress line), the time,
/// the speed and whether it is paused, and the keys. Only the small time label changes as it plays
/// (once a second); the rest is drawn once and cached.
/// </summary>
internal sealed class ReviewOverlay(OverlayPainter painter)
{
    private const float Top = 672, Height = 48, Margin = 16;

    public IEnumerable<RenderQuad> Quads(ReviewClock review)
    {
        var length = review.End - review.Start;
        var done = length > TimeSpan.Zero ? (float)((review.Position - review.Start) / length) : 0;
        var centre = Top + 4 + (Height - 4) / 2;
        var quads = new List<RenderQuad>
        {
            painter.Rect(0, Top, OverlayPainter.StageWidth, Height, new RenderColor(0, 0, 0, 0.6f)),
            painter.Rect(0, Top, OverlayPainter.StageWidth, 4, new RenderColor(1, 1, 1, 0.2f)),
            painter.Rect(0, Top, OverlayPainter.StageWidth * Math.Clamp(done, 0, 1), 4, new RenderColor(1, 0.75f, 0.2f, 1)),
            painter.Text($"{clock(review.Position)} / {clock(review.End)}", Margin, centre, 190, 26, anchor: 0),
            painter.Text($"{review.Speed:0.##}x", Margin + 200, centre, 70, 26, anchor: 0),
            painter.Text(T("review.keys"), OverlayPainter.StageWidth - Margin, centre, 760, 24, anchor: 1, tint: (200, 205, 215)),
        };
        if (review.Paused)
            quads.Add(painter.Text(T("review.paused"), Margin + 280, centre, 160, 26, anchor: 0, tint: (255, 205, 80)));
        return quads;
    }

    private static string clock(TimeSpan time) =>
        $"{(time < TimeSpan.Zero ? "-" : "")}{(int)time.Duration().TotalMinutes}:{time.Duration().Seconds:00}";
}
