using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>Converts a platform-neutral Lumen snapshot into SDL renderer commands.</summary>
public static class LumenRenderFrameAdapter
{
    public static RenderFrame Compose(
        LumenRenderSnapshot snapshot,
        RenderColor clearColor,
        Func<uint, RenderTextureId> resolveTexture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolveTexture);

        var inverseWidth = 1f / snapshot.StageWidth;
        var inverseHeight = 1f / snapshot.StageHeight;
        return new RenderFrame(
            clearColor,
            snapshot.Quads.Select(quad => new RenderQuad(
                resolveTexture(quad.TextureIndex),
                convert(quad.TopLeft, inverseWidth, inverseHeight),
                convert(quad.TopRight, inverseWidth, inverseHeight),
                convert(quad.BottomRight, inverseWidth, inverseHeight),
                convert(quad.BottomLeft, inverseWidth, inverseHeight),
                convert(quad.MultiplyColor),
                convert(quad.AddColor),
                quad.UseNearestSampling ? RenderSampling.Nearest : RenderSampling.Linear)));
    }

    /// <summary>
    /// Frames a standalone movie's visible content while preserving the logical
    /// stage aspect ratio. Empty snapshots fall back to the complete stage.
    /// </summary>
    public static RenderFrame ComposeContentFit(
        LumenRenderSnapshot snapshot,
        RenderColor clearColor,
        Func<uint, RenderTextureId> resolveTexture,
        float occupiedFraction = 0.95f)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolveTexture);
        if (!float.IsFinite(occupiedFraction) || occupiedFraction <= 0 || occupiedFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(occupiedFraction));
        if (snapshot.Quads.IsEmpty)
            return Compose(snapshot, clearColor, resolveTexture);

        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var quad in snapshot.Quads)
        {
            include(quad.TopLeft, ref left, ref top, ref right, ref bottom);
            include(quad.TopRight, ref left, ref top, ref right, ref bottom);
            include(quad.BottomRight, ref left, ref top, ref right, ref bottom);
            include(quad.BottomLeft, ref left, ref top, ref right, ref bottom);
        }

        var contentWidth = right - left;
        var contentHeight = bottom - top;
        if (contentWidth <= 0 || contentHeight <= 0)
            return Compose(snapshot, clearColor, resolveTexture);

        var stageAspect = snapshot.StageWidth / snapshot.StageHeight;
        var viewWidth = contentWidth / occupiedFraction;
        var viewHeight = contentHeight / occupiedFraction;
        if (viewWidth / viewHeight < stageAspect)
            viewWidth = viewHeight * stageAspect;
        else
            viewHeight = viewWidth / stageAspect;

        var centerX = (left + right) * 0.5f;
        var centerY = (top + bottom) * 0.5f;
        var viewLeft = centerX - viewWidth * 0.5f;
        var viewTop = centerY - viewHeight * 0.5f;
        var inverseWidth = 1f / viewWidth;
        var inverseHeight = 1f / viewHeight;
        return new RenderFrame(
            clearColor,
            snapshot.Quads.Select(quad => new RenderQuad(
                resolveTexture(quad.TextureIndex),
                convert(quad.TopLeft, viewLeft, viewTop, inverseWidth, inverseHeight),
                convert(quad.TopRight, viewLeft, viewTop, inverseWidth, inverseHeight),
                convert(quad.BottomRight, viewLeft, viewTop, inverseWidth, inverseHeight),
                convert(quad.BottomLeft, viewLeft, viewTop, inverseWidth, inverseHeight),
                convert(quad.MultiplyColor),
                convert(quad.AddColor),
                quad.UseNearestSampling ? RenderSampling.Nearest : RenderSampling.Linear)));
    }

    private static RenderVertex convert(LumenRenderVertex vertex, float inverseWidth, float inverseHeight) =>
        new(vertex.X * inverseWidth, vertex.Y * inverseHeight, vertex.U, vertex.V);

    private static RenderVertex convert(
        LumenRenderVertex vertex,
        float left,
        float top,
        float inverseWidth,
        float inverseHeight) =>
        new((vertex.X - left) * inverseWidth, (vertex.Y - top) * inverseHeight, vertex.U, vertex.V);

    private static void include(
        LumenRenderVertex vertex,
        ref float left,
        ref float top,
        ref float right,
        ref float bottom)
    {
        left = Math.Min(left, vertex.X);
        top = Math.Min(top, vertex.Y);
        right = Math.Max(right, vertex.X);
        bottom = Math.Max(bottom, vertex.Y);
    }

    private static RenderColor convert(LumenRenderColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);
}
