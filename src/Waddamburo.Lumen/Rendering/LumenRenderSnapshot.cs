using System.Collections.Immutable;

namespace Waddamburo.Lumen.Rendering;

public readonly record struct LumenRenderVertex(float X, float Y, float U, float V);

public readonly record struct LumenRenderColor(float Red, float Green, float Blue, float Alpha)
{
    public static LumenRenderColor White { get; } = new(1f, 1f, 1f, 1f);

    public static LumenRenderColor Transparent { get; } = new(0f, 0f, 0f, 0f);
}

public enum LumenRenderBlend
{
    Normal,
    Add,
}

public readonly record struct LumenRenderQuad(
    uint TextureIndex,
    LumenRenderVertex TopLeft,
    LumenRenderVertex TopRight,
    LumenRenderVertex BottomRight,
    LumenRenderVertex BottomLeft,
    LumenRenderColor MultiplyColor,
    LumenRenderColor AddColor,
    LumenRenderBlend Blend = LumenRenderBlend.Normal,
    bool UseNearestSampling = false);

/// <summary>
/// Immutable renderer-independent output from one Lumen player state. Coordinates
/// use the movie's logical stage; commands retain display-list order.
/// </summary>
public sealed class LumenRenderSnapshot
{
    public LumenRenderSnapshot(float stageWidth, float stageHeight, IEnumerable<LumenRenderQuad> quads)
    {
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));
        ArgumentNullException.ThrowIfNull(quads);

        StageWidth = stageWidth;
        StageHeight = stageHeight;
        Quads = quads.ToImmutableArray();
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public ImmutableArray<LumenRenderQuad> Quads { get; }
}
