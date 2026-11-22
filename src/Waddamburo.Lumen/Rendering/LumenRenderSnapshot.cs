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
    Screen,
    Multiply,
}

public readonly record struct LumenNativeSurfaceKey
{
    public LumenNativeSurfaceKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct LumenNativeSurfacePlacement(float X, float Y, float Width, float Height)
{
    public static LumenNativeSurfacePlacement Centered(float width, float height) =>
        new(-width / 2, -height / 2, width, height);

    /// <summary>
    /// Draws at the marker's average scale, ignoring its shape: some movies' Don marker is a 96x400 placeholder
    /// scaled (2.08, 0.5) into a square, which would stretch the Don into a long drum.
    /// </summary>
    public bool UniformScale { get; init; }
}

/// <summary>Stencil writes never paint color. Depth is the reference before each operation.</summary>
public enum LumenRenderMaskOperation
{
    Draw,
    Push,
    Pop,
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
    bool UseNearestSampling = false,
    LumenNativeSurfaceKey? NativeSurface = null,
    LumenRenderMaskOperation MaskOperation = LumenRenderMaskOperation.Draw,
    byte MaskDepth = 0,
    string? Source = null);

/// <summary>The quads of one snapshot per frame, reused by the next (<see cref="LumenRenderSnapshot.Detached"/> keeps one).</summary>
public sealed class LumenQuadBuffer
{
    internal ImmutableArray<LumenRenderQuad>.Builder Quads { get; } = ImmutableArray.CreateBuilder<LumenRenderQuad>();

    internal int Generation { get; private set; }

    internal ImmutableArray<LumenRenderQuad>.Builder Reuse()
    {
        Generation++;
        Quads.Clear();
        return Quads;
    }
}

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
        _quads = quads.ToImmutableArray();
        Count = _quads.Value.Length;
    }

    private LumenRenderSnapshot(float stageWidth, float stageHeight, LumenQuadBuffer buffer)
    {
        StageWidth = stageWidth;
        StageHeight = stageHeight;
        _buffer = buffer;
        _generation = buffer.Generation;
        Count = buffer.Quads.Count;
    }

    /// <summary>
    /// A snapshot over <paramref name="buffer"/> as it is now, without copying it: valid until the next one is made into
    /// it. Whatever keeps it longer keeps <see cref="Detached"/>.
    /// </summary>
    internal static LumenRenderSnapshot Shared(float stageWidth, float stageHeight, LumenQuadBuffer buffer) =>
        new(stageWidth, stageHeight, buffer);

    private readonly LumenQuadBuffer? _buffer;
    private readonly int _generation;
    private ImmutableArray<LumenRenderQuad>? _quads;

    public float StageWidth { get; }

    public float StageHeight { get; }

    /// <summary>The quads, as an array of their own (a shared snapshot copies them out on first use).</summary>
    public ImmutableArray<LumenRenderQuad> Quads => _quads ??= buffer.ToImmutable();

    public int Count { get; }

    /// <summary>A quad, without copying the rest.</summary>
    public ref readonly LumenRenderQuad this[int index] =>
        ref _quads is null ? ref buffer.ItemRef(index) : ref _quads.Value.ItemRef(index);

    /// <summary>This snapshot with quads of its own (itself when it has them): what keeps it past the next frame holds.</summary>
    public LumenRenderSnapshot Detached() => _buffer is null ? this : new LumenRenderSnapshot(StageWidth, StageHeight, Quads);

    /// <summary>The quads changed one by one: in place in a shared snapshot, a new snapshot otherwise.</summary>
    public LumenRenderSnapshot Map(Func<LumenRenderQuad, LumenRenderQuad> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_buffer is null)
            return new LumenRenderSnapshot(StageWidth, StageHeight, Quads.Select(change));
        var quads = buffer;
        _quads = null; // copied out before the change
        for (var index = 0; index < quads.Count; index++)
            quads[index] = change(quads[index]);
        return this;
    }

    private ImmutableArray<LumenRenderQuad>.Builder buffer => _buffer!.Generation == _generation ? _buffer.Quads
        : throw new InvalidOperationException("A shared snapshot was read after its buffer was reused (keep Detached() instead).");
}
