using System.Collections.Immutable;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

public sealed record LumenSceneLayer(
    LumenPlayer Player,
    LumenMatrix Transform,
    uint TextureOffset,
    uint TextureCount)
{
    /// <summary>The whole movie's opacity on the stage (a host fade, e.g. a modchart's), over its own.</summary>
    public float Alpha { get; init; } = 1;

    /// <summary>Where it holds on a stage wider or narrower than 1280 (LumenStage).</summary>
    public Rendering.LumenAnchor Anchor { get; init; }
}

/// <summary>
/// Composes independently owned Lumen players into one depth-ordered stage.
/// Texture offsets give every child movie a disjoint scene texture namespace.
/// </summary>
public sealed class LumenScenePlayer
{
    private ImmutableArray<LumenSceneLayer> _layers;

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

    /// <summary>Puts another movie in a layer's place (a skin's package saved while its screen is up).</summary>
    public void ReplaceLayer(int index, LumenSceneLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Player.StageWidth != StageWidth || layer.Player.StageHeight != StageHeight)
            throw new ArgumentException("The player must use the scene's logical stage size.", nameof(layer));
        _layers = _layers.SetItem(index, layer);
    }

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
        Func<LumenPlayer, float>? playerInterpolation = null, LumenQuadBuffer? buffer = null)
    {
        if (!float.IsFinite(interpolationFraction) || interpolationFraction < 0 || interpolationFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(interpolationFraction));
        // Gameplay builds a scene player per frame, so the reused builder is static (snapshots are made on one thread).
        // A caller's own buffer is the snapshot's too: no array per frame.
        var quads = buffer?.Reuse() ?? (t_quads ??= ImmutableArray.CreateBuilder<LumenRenderQuad>());
        quads.Clear();
        foreach (var layer in _layers)
        {
            // Each player appends to the scene's builder (no snapshot of its own), then its quads are placed in it.
            var start = quads.Count;
            var placed = Rendering.LumenStage.Anchored(layer.Transform, layer.Anchor);
            layer.Player.SceneAnchor = layer.Anchor; // its clips' anchors hold the stage's edges, not the movie's
            var scaleX = MathF.Sqrt(layer.Transform.M11 * layer.Transform.M11 + layer.Transform.M12 * layer.Transform.M12);
            layer.Player.SceneScale = scaleX > 0 ? scaleX : 1;
            layer.Player.SceneX = layer.Transform.X;
            layer.Player.AppendRenderQuads(playerInterpolation?.Invoke(layer.Player) ?? interpolationFraction, quads);
            // A layer that moves, fades and offsets nothing (the screen's own movie, mostly) keeps its quads as made: a
            // quad holds references, so writing one back is a barriered copy of its ~150 bytes.
            if (placed == LumenMatrix.Identity && layer.Alpha == 1 && layer.TextureOffset == 0)
            {
                for (var index = start; index < quads.Count; index++)
                {
                    ref readonly var made = ref quads.ItemRef(index);
                    if (made.NativeSurface is null && made.TextureIndex >= layer.TextureCount)
                        throw new InvalidOperationException(
                            $"Child snapshot texture {made.TextureIndex} exceeds its {layer.TextureCount}-texture namespace.");
                }
                continue;
            }
            for (var index = start; index < quads.Count; index++)
            {
                var quad = quads[index];
                if (quad.NativeSurface is null && quad.TextureIndex >= layer.TextureCount)
                {
                    throw new InvalidOperationException(
                        $"Child snapshot texture {quad.TextureIndex} exceeds its {layer.TextureCount}-texture namespace.");
                }
                quads[index] = quad with
                {
                    MultiplyColor = layer.Alpha == 1 ? quad.MultiplyColor : quad.MultiplyColor with { Alpha = quad.MultiplyColor.Alpha * layer.Alpha },
                    AddColor = layer.Alpha == 1 ? quad.AddColor : quad.AddColor with { Alpha = quad.AddColor.Alpha * layer.Alpha },
                    TextureIndex = quad.NativeSurface is null
                        ? checked(layer.TextureOffset + quad.TextureIndex)
                        : 0,
                    TopLeft = transform(quad.TopLeft, placed),
                    TopRight = transform(quad.TopRight, placed),
                    BottomRight = transform(quad.BottomRight, placed),
                    BottomLeft = transform(quad.BottomLeft, placed),
                };
            }
        }
        return buffer is null ? new LumenRenderSnapshot(StageWidth, StageHeight, quads.ToImmutable())
            : LumenRenderSnapshot.Shared(StageWidth, StageHeight, buffer);
    }

    [ThreadStatic] private static ImmutableArray<LumenRenderQuad>.Builder? t_quads;

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
