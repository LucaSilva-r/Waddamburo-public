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

public readonly record struct RenderVertex(float X, float Y, float U, float V);

public readonly record struct RenderViewport(int X, int Y, int Width, int Height)
{
    public static RenderViewport AspectFit(uint surfaceWidth, uint surfaceHeight, double contentAspectRatio)
    {
        ArgumentOutOfRangeException.ThrowIfZero(surfaceWidth);
        ArgumentOutOfRangeException.ThrowIfZero(surfaceHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(surfaceWidth, (uint)int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(surfaceHeight, (uint)int.MaxValue);
        if (!double.IsFinite(contentAspectRatio) || contentAspectRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentAspectRatio));

        var width = checked((int)surfaceWidth);
        var height = checked((int)surfaceHeight);
        if ((double)width / height > contentAspectRatio)
        {
            var fittedWidth = Math.Clamp(
                (int)Math.Round(height * (double)contentAspectRatio, MidpointRounding.AwayFromZero),
                1,
                width);
            return new RenderViewport((width - fittedWidth) / 2, 0, fittedWidth, height);
        }

        var fittedHeight = Math.Clamp(
            (int)Math.Round(width / (double)contentAspectRatio, MidpointRounding.AwayFromZero),
            1,
            height);
        return new RenderViewport(0, (height - fittedHeight) / 2, width, fittedHeight);
    }
}

public enum RenderSampling
{
    Nearest,
    Linear,
}

public enum RenderBlend
{
    Normal,
    Add,
}

public readonly record struct RenderQuad(
    RenderTextureId Texture,
    RenderVertex TopLeft,
    RenderVertex TopRight,
    RenderVertex BottomRight,
    RenderVertex BottomLeft,
    RenderColor MultiplyColor,
    RenderColor AddColor,
    RenderBlend Blend = RenderBlend.Normal,
    RenderSampling Sampling = RenderSampling.Linear)
{
    public static RenderQuad FromRectangles(
        RenderTextureId texture,
        RenderRectangle destination,
        RenderRectangle uvRectangle,
        RenderColor multiplyColor,
        RenderColor addColor,
        RenderSampling sampling = RenderSampling.Linear,
        RenderBlend blend = RenderBlend.Normal) =>
        new(
            texture,
            new RenderVertex(destination.X, destination.Y, uvRectangle.X, uvRectangle.Y),
            new RenderVertex(destination.X + destination.Width, destination.Y, uvRectangle.X + uvRectangle.Width, uvRectangle.Y),
            new RenderVertex(destination.X + destination.Width, destination.Y + destination.Height, uvRectangle.X + uvRectangle.Width, uvRectangle.Y + uvRectangle.Height),
            new RenderVertex(destination.X, destination.Y + destination.Height, uvRectangle.X, uvRectangle.Y + uvRectangle.Height),
            multiplyColor,
            addColor,
            blend,
            sampling);
}

/// <summary>Immutable commands for one presentation frame. Coordinates are normalized to the window.</summary>
public sealed class RenderFrame
{
    public RenderFrame(
        RenderColor clearColor,
        IEnumerable<RenderQuad> quads,
        double? contentAspectRatio = null)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (contentAspectRatio is double aspectRatio
            && (!double.IsFinite(aspectRatio) || aspectRatio <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(contentAspectRatio));
        }
        ClearColor = clearColor;
        Quads = quads.ToImmutableArray();
        ContentAspectRatio = contentAspectRatio;
    }

    public RenderColor ClearColor { get; }

    public ImmutableArray<RenderQuad> Quads { get; }

    public double? ContentAspectRatio { get; }

    public RenderViewport ResolveViewport(uint surfaceWidth, uint surfaceHeight) =>
        ContentAspectRatio is double aspectRatio
            ? RenderViewport.AspectFit(surfaceWidth, surfaceHeight, aspectRatio)
            : new RenderViewport(
                0,
                0,
                checked((int)surfaceWidth),
                checked((int)surfaceHeight));
}
