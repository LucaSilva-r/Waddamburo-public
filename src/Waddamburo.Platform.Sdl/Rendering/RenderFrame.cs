using System.Collections.Immutable;

namespace Waddamburo.Platform.Sdl.Rendering;

public readonly record struct RenderColor(float Red, float Green, float Blue, float Alpha)
{
    public static RenderColor White { get; } = new(1f, 1f, 1f, 1f);

    public static RenderColor Transparent { get; } = new(0f, 0f, 0f, 0f);

    public static RenderColor Black { get; } = new(0f, 0f, 0f, 1f);

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
    Screen,
    Multiply,
}

/// <summary>Stencil writes never paint color. Depth is the reference before each operation.</summary>
public enum RenderMaskOperation
{
    Draw,
    Push,
    Pop,
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
    RenderSampling Sampling = RenderSampling.Linear,
    RenderMaskOperation MaskOperation = RenderMaskOperation.Draw,
    byte MaskDepth = 0)
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
        _quads = quads.ToImmutableArray();
        ContentAspectRatio = contentAspectRatio;
    }

    private RenderFrame(RenderColor clearColor, List<RenderQuad> shared, double? contentAspectRatio)
    {
        ClearColor = clearColor;
        _shared = shared;
        _sharedCount = shared.Count;
        ContentAspectRatio = contentAspectRatio;
    }

    /// <summary>
    /// A frame over <paramref name="quads"/> as they are now, without copying them: valid until the list is changed (the
    /// next frame built into it). Whatever keeps a frame longer keeps <see cref="Detached"/>.
    /// </summary>
    // ponytail: one reused list for the window's frame (~30 KB a frame, ~100 MiB/s uncapped); a frame kept longer copies.
    public static RenderFrame Shared(RenderColor clearColor, List<RenderQuad> quads, double? contentAspectRatio = null,
        int blurredQuads = 0, float backdropBrightness = 1)
    {
        ArgumentNullException.ThrowIfNull(quads);
        return new RenderFrame(clearColor, quads, contentAspectRatio) { BlurredQuads = blurredQuads, BackdropBrightness = backdropBrightness };
    }

    private ImmutableArray<RenderQuad>? _quads;
    private readonly List<RenderQuad>? _shared;
    private readonly int _sharedCount;

    public RenderColor ClearColor { get; }

    /// <summary>The quads, as an array of their own (a shared frame copies them out on first use).</summary>
    public ImmutableArray<RenderQuad> Quads => _quads ??= [.. QuadSpan];

    /// <summary>The quads without a copy, for drawing.</summary>
    public ReadOnlySpan<RenderQuad> QuadSpan => _quads is { } own ? own.AsSpan()
        : _shared!.Count == _sharedCount ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_shared)
        : throw new InvalidOperationException("A shared frame was read after its list was reused (keep Detached() instead).");

    /// <summary>This frame with quads of its own (itself when it has them): what a frame kept past the next one holds.</summary>
    public RenderFrame Detached() => _shared is null ? this
        : new RenderFrame(ClearColor, Quads, ContentAspectRatio) { BlurredQuads = BlurredQuads, BackdropBrightness = BackdropBrightness };

    public double? ContentAspectRatio { get; }

    /// <summary>
    /// The first this many quads are a backdrop drawn blurred like frosted glass (an overlay's background), at
    /// <see cref="BackdropBrightness"/>; the rest draw sharp over it. Where the renderer cannot blur, they draw as usual.
    /// </summary>
    public int BlurredQuads { get; init; }

    public float BackdropBrightness { get; init; } = 1;

    public RenderViewport ResolveViewport(uint surfaceWidth, uint surfaceHeight) =>
        ContentAspectRatio is double aspectRatio
            ? RenderViewport.AspectFit(surfaceWidth, surfaceHeight, aspectRatio)
            : new RenderViewport(
                0,
                0,
                checked((int)surfaceWidth),
                checked((int)surfaceHeight));
}
