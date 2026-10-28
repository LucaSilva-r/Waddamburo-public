using System.Collections.Immutable;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>Converts a platform-neutral Lumen snapshot into SDL renderer commands.</summary>
public static class LumenRenderFrameAdapter
{
    public static RenderFrame Compose(
        LumenRenderSnapshot snapshot,
        RenderColor clearColor,
        Func<uint, RenderTextureId> resolveTexture,
        Func<LumenNativeSurfaceKey, RenderTextureId?>? resolveNativeSurface = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolveTexture);

        var inverseWidth = 1f / snapshot.StageWidth;
        var inverseHeight = 1f / snapshot.StageHeight;
        // Exact capacity: MoveToImmutable hands the array to the frame without a copy.
        var quads = ImmutableArray.CreateBuilder<RenderQuad>(snapshot.Quads.Length);
        RenderPixelFrame? pixelFrame = null;
        foreach (var quad in snapshot.Quads)
        {
            if (quad.Source is LumenVectorGeometry.FrameAnchor or LumenVectorGeometry.PegAnchor)
            {
                var box = LumenNativeFrame.Bounds(quad);
                pixelFrame = new(new(box.X * inverseWidth, box.Y * inverseHeight,
                    box.Width * inverseWidth, box.Height * inverseHeight),
                    quad.Source == LumenVectorGeometry.PegAnchor ? LumenVectorGeometry.FrameBorder * quad.FrameBorderScale * inverseHeight : 0);
                continue;
            }
            var frameSurface = pixelFrame is not null && quad.NativeSurface is { } paint && paint != LumenVectorGeometry.Shadow
                && (paint == LumenVectorGeometry.White || paint == LumenVectorGeometry.Coverage
                    || paint.Value.StartsWith("vector-ui:paint:", StringComparison.Ordinal)
                    || paint.Value == "folder-metallic-gold");
            var rasterFrame = frameSurface ? pixelFrame : null;
            if (quad.Source == LumenVectorGeometry.FrameEnd) pixelFrame = null;
            RenderTextureId? texture = quad.NativeSurface is { } surface
                ? (resolveNativeSurface ?? throw new InvalidOperationException(
                    $"Native Lumen surface '{surface}' has no platform resolver."))(surface)
                : resolveTexture(quad.TextureIndex);
            if (texture is null)
                continue;

            quads.Add(new RenderQuad(
                texture.Value,
                convert(quad.TopLeft, inverseWidth, inverseHeight),
                convert(quad.TopRight, inverseWidth, inverseHeight),
                convert(quad.BottomRight, inverseWidth, inverseHeight),
                convert(quad.BottomLeft, inverseWidth, inverseHeight),
                convert(quad.MultiplyColor),
                convert(quad.AddColor),
                convert(quad.Blend),
                quad.UseNearestSampling ? RenderSampling.Nearest : RenderSampling.Linear,
                quad.MaskOperation switch
                {
                    LumenRenderMaskOperation.Draw => RenderMaskOperation.Draw,
                    LumenRenderMaskOperation.Push => RenderMaskOperation.Push,
                    LumenRenderMaskOperation.Pop => RenderMaskOperation.Pop,
                    _ => throw new ArgumentOutOfRangeException(nameof(snapshot)),
                }, quad.MaskDepth, rasterFrame));
        }
        return new RenderFrame(
            clearColor,
            quads.Count == quads.Capacity ? quads.MoveToImmutable() : quads.ToImmutable(),
            (double)snapshot.StageWidth / snapshot.StageHeight);
    }

    private static RenderVertex convert(LumenRenderVertex vertex, float inverseWidth, float inverseHeight) =>
        new(vertex.X * inverseWidth, vertex.Y * inverseHeight, vertex.U, vertex.V);

    private static RenderColor convert(LumenRenderColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);

    private static RenderBlend convert(LumenRenderBlend blend) => blend switch
    {
        LumenRenderBlend.Normal => RenderBlend.Normal,
        LumenRenderBlend.Add => RenderBlend.Add,
        LumenRenderBlend.Screen => RenderBlend.Screen,
        LumenRenderBlend.Multiply => RenderBlend.Multiply,
        _ => throw new ArgumentOutOfRangeException(nameof(blend)),
    };
}
