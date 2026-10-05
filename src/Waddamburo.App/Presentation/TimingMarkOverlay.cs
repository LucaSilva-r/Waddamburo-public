using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// A glow rising from the line under a lane's hit target after a hit (Goods and Bads, or every hit:
/// <see cref="TimingIndicator"/>): blue when the hit was
/// early, red when late, so a player can tell their timing from the game's. Flashes in and fades over
/// <see cref="Duration"/>.
/// </summary>
internal sealed class TimingMarkOverlay : IDisposable
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(500);
    // ponytail: the lane's bottom line, measured on Green's lane as target radii below the target's
    // centre; another skin may want its own.
    private const float LaneBottom = 1.45f, WidthRadii = 2.6f, HeightRadii = 1.1f;
    private static readonly RenderColor Early = new(0.15f, 0.55f, 1f, 1), Late = new(1f, 0.12f, 0.08f, 1);
    private readonly SdlApplication _application;
    private readonly RenderTextureId _glow;

    public TimingMarkOverlay(SdlApplication application)
    {
        _application = application;
        // White, cut flat at the bottom edge where it is strongest, fading upward and to the sides.
        const int width = 128, height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float dx = (x + 0.5f) / width * 2 - 1, up = 1 - (y + 0.5f) / height;
                var falloff = Math.Max(0, 1 - (dx * dx + up * up));
                var index = (y * width + x) * 4;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
                // A solid core that only softens near its edge.
                pixels[index + 3] = (byte)(255 * Math.Min(1, falloff * 1.8f));
            }
        _glow = application.UploadRgba8(width, height, pixels);
    }

    public IEnumerable<RenderQuad> Quads(IEnumerable<TimingMark> marks, TimingIndicator shown)
    {
        var quads = new List<RenderQuad>();
        foreach (var mark in marks)
        {
            if (shown == TimingIndicator.Off || mark.Great && shown != TimingIndicator.Always)
                continue;
            var age = Stopwatch.GetElapsedTime(mark.At);
            if (age > Duration)
                continue;
            // A quick flash in (50 ms), held, then a fade over the last 60 %.
            var progress = (float)(age / Duration);
            var strength = Math.Min(1, (float)(age.TotalMilliseconds / 50)) * Math.Min(1, (1 - progress) / 0.6f);
            float width = mark.Radius * WidthRadii, height = mark.Radius * HeightRadii, bottom = mark.Y + mark.Radius * LaneBottom;
            quads.Add(RenderQuad.FromRectangles(_glow,
                new((mark.X - width / 2) / 1280, (bottom - height) / 720, width / 1280, height / 720),
                RenderRectangle.Full, (mark.Early ? Early : Late) with { Alpha = strength }, RenderColor.Transparent));
        }
        return quads;
    }

    public void Dispose() => _application.ReleaseTexture(_glow);
}
