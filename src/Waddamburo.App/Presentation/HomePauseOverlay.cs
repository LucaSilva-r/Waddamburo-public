using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Presentation;

/// <summary>
/// What the home controls draw besides their menus (the overlay menu draws those): the fade to black of a restart or
/// a scene change, and the audio calibration's instructions under its lane.
/// </summary>
internal sealed class HomePauseOverlay(OverlayPainter painter)
{
    public IEnumerable<RenderQuad> Quads(float black, LatencyCalibration? calibration = null)
    {
        var quads = new List<RenderQuad>();
        if (calibration is not null)
        {
            // Under the lane: what to do, and the offset being tried.
            quads.Add(painter.Rect(0, 580, 1280, 100, new(0, 0, 0, 0.7f)));
            quads.Add(painter.Text(T("calibration.instructions", signed(calibration.AudioOffsetMs)), 640, 612, 1100, 36));
            quads.Add(painter.Text(T("calibration.controls"), 640, 652, 1100, 28));
        }
        if (black > 0)
            quads.Add(painter.Rect(0, 0, 1280, 720, new(0, 0, 0, Math.Clamp(black, 0, 1))));
        return quads;
    }

    private static string signed(int ms) => T("value.ms", ms.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The bake page's two lines: what it is doing, and how far along.</summary>
    public static string[] bakeText(TextureBake bake)
    {
        static string time(TimeSpan span) => span.TotalHours >= 1 ? T("time.hours", (int)span.TotalHours, span.Minutes)
            : span.TotalMinutes >= 1 ? T("time.minutes", (int)span.TotalMinutes) : T("time.seconds", Math.Max(1, (int)span.TotalSeconds));
        return bake.Phase switch
        {
            TextureBake.BakePhase.Scanning => [T("bake.scanning", bake.ArchivesScanned, bake.ArchivesTotal),
                T("bake.already", bake.TexturesCached)],
            TextureBake.BakePhase.Upscaling => [T("bake.progress", bake.TexturesDone, bake.TexturesTotal),
                bake.Remaining is { } left
                    ? T("bake.speed", bake.MegapixelsPerSecond, bake.Threads, time(left))
                    : T("bake.starting", bake.Threads)],
            TextureBake.BakePhase.Done => [bake.TexturesTotal == 0 ? T("bake.nothing")
                : T("bake.done", bake.TexturesDone), T("bake.took", time(bake.Elapsed))],
            TextureBake.BakePhase.Stopped => [T("bake.stopped", bake.TexturesDone),
                T("bake.left", bake.TexturesTotal - bake.TexturesDone)],
            _ => [T("bake.failed"), bake.Error ?? ""],
        };
    }
}
