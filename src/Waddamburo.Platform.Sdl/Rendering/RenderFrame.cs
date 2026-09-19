using System.Collections.Immutable;

namespace Waddamburo.Platform.Sdl.Rendering;

public readonly record struct RenderColor(float Red, float Green, float Blue, float Alpha)
{
    public static RenderColor White { get; } = new(1f, 1f, 1f, 1f);

    public static RenderColor Transparent { get; } = new(0f, 0f, 0f, 0f);

    public static RenderColor WaddamburoBlue { get; } = new(0.035f, 0.075f, 0.14f, 1f);
}

public readonly record struct RenderRectangle(float X, float Y, float Width, float Height)
{
    public static RenderRectangle Full { get; } = new(0f, 0f, 1f, 1f);
}

public readonly record struct RenderTextureId(uint Value);

public enum RenderSampling
{
    Nearest,
    Linear,
}

public readonly record struct RenderQuad(
    RenderTextureId Texture,
    RenderRectangle Destination,
    RenderRectangle UvRectangle,
    RenderColor MultiplyColor,
    RenderColor AddColor,
    RenderSampling Sampling = RenderSampling.Linear);

/// <summary>Immutable commands for one presentation frame. Coordinates are normalized to the window.</summary>
public sealed class RenderFrame
{
    public RenderFrame(RenderColor clearColor, IEnumerable<RenderQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        ClearColor = clearColor;
        Quads = quads.ToImmutableArray();
    }

    public RenderColor ClearColor { get; }

    public ImmutableArray<RenderQuad> Quads { get; }
}
