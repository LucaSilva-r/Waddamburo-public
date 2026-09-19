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

    private static RenderVertex convert(LumenRenderVertex vertex, float inverseWidth, float inverseHeight) =>
        new(vertex.X * inverseWidth, vertex.Y * inverseHeight, vertex.U, vertex.V);

    private static RenderColor convert(LumenRenderColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);
}
