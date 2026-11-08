using System.Diagnostics;
using Waddamburo.App.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A reviewed play's bar along the bottom of the screen: how far it is (a progress line), the time,
/// the speed and whether it is paused, and the keys. Only the small time label changes as it plays
/// (once a second); the rest is drawn once and cached. Training adds its loop's marks, the last attempt
/// and its own keys.
/// </summary>
internal sealed class ReviewOverlay(OverlayPainter painter)
{
    private const float Top = 672, Height = 48, Margin = 16;
    private static readonly long ShowFor = Stopwatch.Frequency * 3;

    private ReviewClock? _for;
    private long _shownUntil, _last;
    private float _slide = 1;

    /// <summary>A key was pressed: an auto-hiding bar slides up for a few seconds.</summary>
    public void Poke() => _shownUntil = Stopwatch.GetTimestamp() + ShowFor;

    /// <summary>
    /// <paramref name="autoHide"/>: the autoplay, watched like a video: the bar starts hidden under the screen and
    /// slides up while paused or for a few seconds after a key.
    /// </summary>
    public IEnumerable<RenderQuad> Quads(ReviewClock review, TrainingSession? training, bool autoHide)
    {
        var now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(review, _for))
            (_for, _slide, _shownUntil) = (review, autoHide ? 0 : 1, 0);
        var step = Math.Min(0.1f, (float)((now - _last) / (double)Stopwatch.Frequency));
        _last = now;
        _slide = !autoHide || review.Paused || now < _shownUntil ? Math.Min(1, _slide + step / 0.2f) : Math.Max(0, _slide - step / 0.3f);
        if (_slide <= 0)
            return [];
        var drop = (Height + 12) * MathF.Pow(1 - _slide, 3); // ease-out
        var quads = bar(review, training);
        return drop < 0.01f ? quads : quads.Select(quad => quad with
        {
            TopLeft = quad.TopLeft with { Y = quad.TopLeft.Y + drop },
            TopRight = quad.TopRight with { Y = quad.TopRight.Y + drop },
            BottomRight = quad.BottomRight with { Y = quad.BottomRight.Y + drop },
            BottomLeft = quad.BottomLeft with { Y = quad.BottomLeft.Y + drop },
        });
    }

    private List<RenderQuad> bar(ReviewClock review, TrainingSession? training)
    {
        var length = review.End - review.Start;
        var done = length > TimeSpan.Zero ? (float)((review.Position - review.Start) / length) : 0;
        var centre = Top + 4 + (Height - 4) / 2;
        var quads = new List<RenderQuad>
        {
            painter.Rect(0, Top, OverlayPainter.StageWidth, Height, new RenderColor(0, 0, 0, 0.6f)),
            painter.Rect(0, Top, OverlayPainter.StageWidth, 4, new RenderColor(1, 1, 1, 0.2f)),
            painter.Rect(0, Top, OverlayPainter.StageWidth * Math.Clamp(done, 0, 1), 4, new RenderColor(1, 0.75f, 0.2f, 1)),
            painter.Text($"{review.Speed:0.00}x", Margin + 200, centre, 70, 26, anchor: 0),
        };
        // A new text every second, drawn on a worker (the painter keeps the last one up meanwhile).
        quads.Add(painter.Text($"{clock(review.Position)} / {clock(review.End)}", Margin, centre, 190, 26, anchor: 0, slot: this));
        float at(TimeSpan time) => OverlayPainter.StageWidth * (float)Math.Clamp((time - review.Start) / length, 0, 1);
        if (training is null)
        {
            quads.Add(painter.Text(T("review.keys", GameActions.PauseKeys, GameActions.Name(GameAction.Practise)), OverlayPainter.StageWidth - Margin, centre, 760, 24, anchor: 1, tint: (200, 205, 215)));
            if (review.Paused)
                quads.Add(painter.Text(T("review.paused"), Margin + 280, centre, 160, 26, anchor: 0, tint: (255, 205, 80)));
            return quads;
        }
        // The loop: its stretch lit on the progress line, a tall mark at each end.
        var (start, end) = (training.LoopStart ?? review.Start, training.LoopEnd ?? review.End);
        if (training.LoopStart is not null || training.LoopEnd is not null)
            quads.Add(painter.Rect(at(start), Top, at(end) - at(start), 4, new RenderColor(0.35f, 0.75f, 1, 0.8f)));
        foreach (var mark in new[] { training.LoopStart, training.LoopEnd }.OfType<TimeSpan>())
            quads.Add(painter.Rect(at(mark) - 1.5f, Top - 10, 3, 14, new RenderColor(0.35f, 0.75f, 1, 1)));
        var status = training.LastAttempt is var (great, good, miss) ? T("training.last", great, good, miss)
            : review.Paused ? T("review.paused") : T("training.playing");
        quads.Add(painter.Text(status, Margin + 280, centre, 220, 24, anchor: 0, tint: (255, 205, 80)));
        quads.Add(painter.Text(T(training.PauseAfterAttempt ? "training.keys_pause" : "training.keys_loop", GameActions.PauseKeys, GameActions.Name(GameAction.NoteJump),
                GameActions.Name(GameAction.LoopStart), GameActions.Name(GameAction.LoopEnd), GameActions.Name(GameAction.AfterPass)),
            OverlayPainter.StageWidth - Margin, centre, 740, 22, anchor: 1, tint: (200, 205, 215)));
        return quads;
    }

    private static string clock(TimeSpan time) =>
        $"{(time < TimeSpan.Zero ? "-" : "")}{(int)time.Duration().TotalMinutes}:{time.Duration().Seconds:00}";
}
