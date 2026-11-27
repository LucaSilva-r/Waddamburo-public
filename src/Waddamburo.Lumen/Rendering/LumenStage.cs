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
    /// <summary>
    /// Stretched across: its left side keeps its distance to the left edge, its right side to the right edge (a
    /// background, a bar, a lane's band). A 9-sliced shape keeps its corners and caps.
    /// </summary>
    Stretch,
    /// <summary>Scaled evenly to cover the stage, the overflow cropped (a picture background, as osu!'s).</summary>
    Cover,
    /// <summary>Its right side reaches the right edge, at any width; its left side stays (a lane from the judge circle on).</summary>
    StretchRight,
    /// <summary>Its left side reaches the left edge, at any width; its right side stays (the panel left of the judge circle).</summary>
    StretchLeft,
    /// <summary>
    /// Left on a narrower stage, centred on a wider one: gameplay, whose judge circle stays where a 16:9 screen in the
    /// middle would have it (the lane and its panel stretch out to the edges).
    /// </summary>
    LeftWhenNarrow,
}

/// <summary>
/// The stage's size. Always 720 units tall; as wide as the screen's shape asks (<see cref="Fit"/>: 960 at 4:3, 1280 at
/// 16:9, 1680 at 21:9, any wider screen filled; narrower than 4:3, letterboxed). Everything is still made and placed on a 1280-wide stage, centred in the
/// real one; an anchored layer moves with its edge
/// (<see cref="Anchored"/>). A stage of 1280 changes nothing (Green's frontend, the tools).
/// </summary>
public static class LumenStage
{
    // As osu!: any screen wider than 4:3 is filled (backgrounds stretched, anchored parts at the edges, the rest
    // centred); 8:1 is only a sanity bound. Narrower than 4:3 (a portrait window) is letterboxed.
    public const float BaseWidth = 1280, Height = 720, MinWidth = 960, MaxWidth = 5760;

    /// <summary>The stage's width now (set each frame by the game from its window).</summary>
    public static float Width { get; set; } = BaseWidth;

    /// <summary>How far the stage reaches past the 1280 box on each side (negative: it is narrower).</summary>
    public static float Extra => (Width - BaseWidth) / 2;

    /// <summary>The width a screen of this shape (width / height) gets.</summary>
    public static float Fit(double aspect) => (float)Math.Clamp(Height * aspect, MinWidth, MaxWidth);

    /// <summary>A layer's transform (on the 1280 stage) moved or stretched by its anchor for the stage now.</summary>
    public static LumenMatrix Anchored(LumenMatrix transform, LumenAnchor anchor)
    {
        if (anchor == LumenAnchor.LeftWhenNarrow)
            anchor = Width < BaseWidth ? LumenAnchor.Left : LumenAnchor.Center;
        if (anchor is LumenAnchor.Center or LumenAnchor.StretchLeft or LumenAnchor.StretchRight || Width == BaseWidth)
            return transform;
        if (anchor == LumenAnchor.Cover)
        {
            // Evenly about the stage's centre, as much as covers it (a narrower stage is covered already: cropped).
            var cover = Math.Max(1, Width / BaseWidth);
            return new LumenMatrix(transform.M11 * cover, transform.M12 * cover, transform.M21 * cover, transform.M22 * cover,
                BaseWidth / 2 + (transform.X - BaseWidth / 2) * cover, Height / 2 + (transform.Y - Height / 2) * cover);
        }
        if (anchor == LumenAnchor.Stretch)
        {
            // Wider about the 1280 box's centre: its edges land on the stage's.
            var scale = Width / BaseWidth;
            return transform with { M11 = transform.M11 * scale, M21 = transform.M21 * scale, X = BaseWidth / 2 + (transform.X - BaseWidth / 2) * scale };
        }
        return transform with { X = transform.X + (anchor == LumenAnchor.Left ? -Extra : Extra) };
    }

    /// <summary>How far a movie anchored so is moved sideways (left, right; none else).</summary>
    public static float Shift(LumenAnchor anchor) => anchor switch
    {
        LumenAnchor.Left => -Extra,
        LumenAnchor.Right => Extra,
        LumenAnchor.LeftWhenNarrow when Width < BaseWidth => -Extra,
        _ => 0,
    };

    /// <summary>
    /// A clip's transform in its movie (whose own scene anchor is <paramref name="movie"/>) for the clip's anchor, kept
    /// to the stage's edges whatever the movie's anchor (a left clip in a right-anchored movie still holds the left edge).
    /// Stretch is not a transform: the player maps the clip's drawing (<see cref="StretchSpan"/>).
    /// </summary>
    public static LumenMatrix ClipAnchored(LumenMatrix transform, LumenAnchor clip, LumenAnchor movie, float movieScale = 1)
    {
        if (clip == LumenAnchor.LeftWhenNarrow)
            clip = Width < BaseWidth ? LumenAnchor.Left : LumenAnchor.Center;
        if (Width == BaseWidth || clip is LumenAnchor.Center or LumenAnchor.Stretch or LumenAnchor.StretchLeft or LumenAnchor.StretchRight)
            return transform;
        // In the movie's own units: a movie drawn at 0.9 moves its clips 1/0.9 as far for the same distance on the stage.
        var shift = Shift(movie) / movieScale;
        if (clip != LumenAnchor.Cover)
            return transform with { X = transform.X + (Shift(clip) / movieScale - shift) };
        var covered = Anchored(transform with { X = transform.X + shift }, LumenAnchor.Cover);
        return covered with { X = covered.X - shift };
    }

    /// <summary>
    /// Where a stretched clip drawn from <paramref name="minX"/> to <paramref name="maxX"/> (in its movie, anchored
    /// <paramref name="movie"/>) goes: each side keeps its distance to its edge of the stage.
    /// </summary>
    /// <remarks>
    /// stretch keeps each side's distance to its edge; stretch-right / stretch-left take that side to the edge itself, at
    /// any width (<paramref name="movieX"/>, <paramref name="movieScale"/>: where and how wide the scene draws the movie).
    /// </remarks>
    public static (float From, float To) StretchSpan(float minX, float maxX, LumenAnchor movie, LumenAnchor clip = LumenAnchor.Stretch,
        float movieScale = 1, float movieX = 0) => clip switch
    {
        LumenAnchor.StretchRight => (minX, (BaseWidth + Extra - Shift(movie) - movieX) / movieScale),
        LumenAnchor.StretchLeft => ((-Extra - Shift(movie) - movieX) / movieScale, maxX),
        _ => (minX - (Extra + Shift(movie)) / movieScale, maxX + (Extra - Shift(movie)) / movieScale),
    };

    /// <summary>Whether the anchor widens the drawing (a stretch of either kind) rather than moving it.</summary>
    public static bool Stretches(LumenAnchor anchor) => anchor is LumenAnchor.Stretch or LumenAnchor.StretchLeft or LumenAnchor.StretchRight;

    /// <summary>
    /// How far a thing centred at <paramref name="centre"/> (a fraction of the 1280 stage) moves to keep to its nearest
    /// side (touch buttons): the left third left, the right third right, the middle stays. A fraction of 1280.
    /// </summary>
    public static float SideShift(float centre) => centre < .4f ? -Extra / BaseWidth : centre > .6f ? Extra / BaseWidth : 0;

    /// <summary>A touch (a fraction of the 1280 stage) back to where a side-kept thing was made (<see cref="SideShift"/>).</summary>
    public static float FromSides(float x) =>
        x + Extra / BaseWidth < .4f ? x + Extra / BaseWidth : x - Extra / BaseWidth > .6f ? x - Extra / BaseWidth : x;

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
        "cover" => LumenAnchor.Cover,
        "stretch-right" => LumenAnchor.StretchRight,
        "stretch-left" => LumenAnchor.StretchLeft,
        "left-when-narrow" => LumenAnchor.LeftWhenNarrow,
        _ => throw new FormatException($"\"anchor\" is left, center, right, stretch, stretch-left, stretch-right, cover or left-when-narrow, not \"{text}\"."),
    };

    public static string Name(LumenAnchor anchor) => anchor switch
    {
        LumenAnchor.StretchRight => "stretch-right",
        LumenAnchor.StretchLeft => "stretch-left",
        LumenAnchor.LeftWhenNarrow => "left-when-narrow",
        _ => anchor.ToString().ToLowerInvariant(),
    };
}
