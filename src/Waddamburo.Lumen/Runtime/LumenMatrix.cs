namespace Waddamburo.Lumen.Runtime;

public readonly record struct LumenMatrix(float M11, float M12, float M21, float M22, float X, float Y)
{
    public static LumenMatrix Identity { get; } = new(1f, 0f, 0f, 1f, 0f, 0f);

    public (float X, float Y) Transform(float x, float y) =>
        (M11 * x + M21 * y + X, M12 * x + M22 * y + Y);

    /// <summary>Composes this local transform followed by its parent transform.</summary>
    public LumenMatrix Then(LumenMatrix parent) =>
        new(
            parent.M11 * M11 + parent.M21 * M12,
            parent.M12 * M11 + parent.M22 * M12,
            parent.M11 * M21 + parent.M21 * M22,
            parent.M12 * M21 + parent.M22 * M22,
            parent.M11 * X + parent.M21 * Y + parent.X,
            parent.M12 * X + parent.M22 * Y + parent.Y);
}
