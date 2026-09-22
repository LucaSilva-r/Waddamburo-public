using System.Collections.Immutable;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

public sealed record LumenSceneLayer(
    LumenPlayer Player,
    LumenMatrix Transform,
    uint TextureOffset,
    uint TextureCount);

/// <summary>
/// Composes independently owned Lumen players into one depth-ordered stage.
/// Texture offsets give every child movie a disjoint scene texture namespace.
/// </summary>
public sealed class LumenScenePlayer
{
    private readonly ImmutableArray<LumenSceneLayer> _layers;

    public LumenScenePlayer(
        float stageWidth,
        float stageHeight,
        IEnumerable<LumenSceneLayer> layers)
    {
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));
        ArgumentNullException.ThrowIfNull(layers);

        StageWidth = stageWidth;
        StageHeight = stageHeight;
        _layers = layers.ToImmutableArray();
        foreach (var layer in _layers)
        {
            ArgumentNullException.ThrowIfNull(layer.Player);
            if (layer.Player.StageWidth != stageWidth || layer.Player.StageHeight != stageHeight)
                throw new ArgumentException("Every child player must use the scene's logical stage size.", nameof(layers));
            validateTransform(layer.Transform, nameof(layers));
            _ = checked(layer.TextureOffset + layer.TextureCount);
        }
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public ImmutableArray<LumenSceneLayer> Layers => _layers;

    public void Advance()
    {
        foreach (var layer in _layers)
            layer.Player.Advance();
    }

    public void Advance(LumenInputSnapshot inputSnapshot)
    {
        ArgumentNullException.ThrowIfNull(inputSnapshot);
        foreach (var layer in _layers)
            layer.Player.Advance(inputSnapshot);
    }

    public LumenRenderSnapshot CreateRenderSnapshot(float interpolationFraction = 1f,
        Func<LumenPlayer, float>? playerInterpolation = null)
    {
        if (!float.IsFinite(interpolationFraction) || interpolationFraction < 0 || interpolationFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(interpolationFraction));
        var quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
        foreach (var layer in _layers)
        {
            var child = layer.Player.CreateRenderSnapshot(playerInterpolation?.Invoke(layer.Player) ?? interpolationFraction);
            foreach (var quad in child.Quads)
            {
                if (quad.NativeSurface is null && quad.TextureIndex >= layer.TextureCount)
                {
                    throw new InvalidOperationException(
                        $"Child snapshot texture {quad.TextureIndex} exceeds its {layer.TextureCount}-texture namespace.");
                }
                quads.Add(quad with
                {
                    TextureIndex = quad.NativeSurface is null
                        ? checked(layer.TextureOffset + quad.TextureIndex)
                        : 0,
                    TopLeft = transform(quad.TopLeft, layer.Transform),
                    TopRight = transform(quad.TopRight, layer.Transform),
                    BottomRight = transform(quad.BottomRight, layer.Transform),
                    BottomLeft = transform(quad.BottomLeft, layer.Transform),
                });
            }
        }
        return new LumenRenderSnapshot(StageWidth, StageHeight, quads.ToImmutable());
    }

    private static LumenRenderVertex transform(LumenRenderVertex vertex, LumenMatrix matrix)
    {
        var position = matrix.Transform(vertex.X, vertex.Y);
        return vertex with { X = position.X, Y = position.Y };
    }

    private static void validateTransform(LumenMatrix transform, string parameterName)
    {
        if (!float.IsFinite(transform.M11) || !float.IsFinite(transform.M12)
            || !float.IsFinite(transform.M21) || !float.IsFinite(transform.M22)
            || !float.IsFinite(transform.X) || !float.IsFinite(transform.Y))
        {
            throw new ArgumentException("Scene transforms must contain finite values.", parameterName);
        }
    }
}
