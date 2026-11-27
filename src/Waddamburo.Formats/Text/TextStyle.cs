using System.Collections.Immutable;

namespace Waddamburo.Formats.Text;

/// <summary>Where text sits along its line (start = left, or top when vertical), or across its lines.</summary>
public enum TextAlign { Start, Center, End }

/// <summary>What a text too big for its box does.</summary>
public enum TextFit
{
    /// <summary>Nothing: it runs out of the box.</summary>
    Clip,
    /// <summary>The whole font shrinks.</summary>
    Shrink,
    /// <summary>Squeezed along the line only (as the game's titles).</summary>
    Squeeze,
    /// <summary>Broken into lines, then shrunk if still too big.</summary>
    Wrap,
}

/// <summary>
/// One layer of a text's look: its shape grown outwards by <see cref="Grow"/> (0: the glyphs themselves), moved,
/// blurred, and painted from <see cref="Top"/> to <see cref="Bottom"/> (0xRRGGBBAA) along <see cref="Angle"/> degrees
/// (0: top to bottom, 90: left to right), <see cref="Top"/> up to <see cref="From"/> and <see cref="Bottom"/> from
/// <see cref="To"/> (0..1 across each line, or the whole text with <see cref="OverText"/>). A title with a shadow, a
/// black border, a red border and a white fill is four layers, back to front.
/// </summary>
public sealed record TextLayer(float Grow = 0, float OffsetX = 0, float OffsetY = 0, float Blur = 0,
    uint Top = 0xffffffff, uint Bottom = 0xffffffff, float Angle = 0, float From = 0, float To = 1, bool OverText = false)
{
    public static TextLayer Solid(uint rgba, float grow = 0) => new(grow, Top: rgba, Bottom: rgba);
}

/// <summary>
/// How a text looks and sits in its box. Lengths are in the box's own units (the movie's pixels).
/// <see cref="Font"/> names a font the host resolves (the skin's, or the game's).
/// </summary>
public sealed record TextStyle(
    string Font,
    float Size,
    ImmutableArray<TextLayer> Layers,
    float LetterSpacing = 0,
    float LineHeight = 0,
    float Slant = 0,
    TextAlign Align = TextAlign.Start,
    TextAlign VerticalAlign = TextAlign.Center,
    TextFit Fit = TextFit.Shrink,
    bool Vertical = false)
{
    /// <summary>How far the layers reach out of the text (grow + blur + offset): the renderer keeps the text that far in.</summary>
    public float Reach => Layers.IsDefaultOrEmpty ? 0 : Layers.Max(static layer =>
        Math.Max(layer.Grow, 0) + Math.Max(layer.Blur, 0) + Math.Max(Math.Abs(layer.OffsetX), Math.Abs(layer.OffsetY)));
}
