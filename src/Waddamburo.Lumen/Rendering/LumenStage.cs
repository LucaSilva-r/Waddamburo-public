using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Rendering;

/// <summary>Where a scene layer (or a clip) holds when the stage is wider or narrower than 1280.</summary>
public enum LumenAnchor
{
    /// <summary>Centred with the stage (the default: what was made for 1280 stays in the middle).</summary>
    Center,
    /// <summary>Keeps its distance to the left edge.</summary>
    Left,
    /// <summary>Keeps its distance to the right edge.</summary>
    Right,
    /// <summary>Stretched across: as wide as the stage (backgrounds, bars).</summary>
    Stretch,
}

/// <summary>
/// The stage's size. Always 720 units tall; as wide as the screen's shape asks (<see cref="Fit"/>: 960 at 4:3, 1280 at
/// 16:9, 1680 at 21:9; past that, letterboxed). Everything is still made and placed on a 1280-wide stage, centred in the
/// real one (the frame is widened at the end: <see cref="Widen"/>); an anchored layer moves with its edge
/// (<see cref="Anchored"/>). A stage of 1280 changes nothing (Green's frontend, the tools).
/// </summary>
public static class LumenStage
{
    public const float BaseWidth = 1280, Height = 720, MinWidth = 960, MaxWidth = 1680;

    /// <summary>The stage's width now (set each frame by the game from its window).</summary>
    public static float Width { get; set; } = BaseWidth;

    /// <summary>How far the stage reaches past the 1280 box on each side (negative: it is narrower).</summary>
    public static float Extra => (Width - BaseWidth) / 2;

    /// <summary>The width a screen of this shape (width / height) gets.</summary>
    public static float Fit(double aspect) => (float)Math.Clamp(Height * aspect, MinWidth, MaxWidth);

    /// <summary>A layer's transform (on the 1280 stage) moved or stretched by its anchor for the stage now.</summary>
    public static LumenMatrix Anchored(LumenMatrix transform, LumenAnchor anchor)
    {
        if (anchor == LumenAnchor.Center || Width == BaseWidth)
            return transform;
        if (anchor == LumenAnchor.Stretch)
        {
            // Wider about the 1280 box's centre: its edges land on the stage's.
            var scale = Width / BaseWidth;
            return transform with { M11 = transform.M11 * scale, M21 = transform.M21 * scale, X = BaseWidth / 2 + (transform.X - BaseWidth / 2) * scale };
        }
        return transform with { X = transform.X + (anchor == LumenAnchor.Left ? -Extra : Extra) };
    }

    /// <summary>A point of the 1280 stage on the real one, as a fraction of its width.</summary>
    public static float ToStage(float baseX) => (baseX + Extra) / Width;

    /// <summary>A fraction of the real stage's width as a point of the 1280 stage (a touch, the pointer).</summary>
    public static float FromStage(float fraction) => fraction * Width - Extra;

    public static LumenAnchor Parse(string? text) => text switch
    {
        null or "center" => LumenAnchor.Center,
        "left" => LumenAnchor.Left,
        "right" => LumenAnchor.Right,
        "stretch" => LumenAnchor.Stretch,
        _ => throw new FormatException($"\"anchor\" is left, center, right or stretch, not \"{text}\"."),
    };

    public static string Name(LumenAnchor anchor) => anchor.ToString().ToLowerInvariant();
}
