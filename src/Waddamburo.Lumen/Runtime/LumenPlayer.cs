using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// Small deterministic display-list core. It currently applies ordinary-frame
/// placement/removal records and builds render snapshots; AVM actions and key-frame
/// interpolation remain explicit diagnostics until their runtime modules exist.
/// </summary>
public sealed class LumenPlayer
{
    private readonly LmbMovieDefinition _movie;
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, SpriteTimeline> _sprites;
    private readonly List<LumenRuntimeDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly DisplayInstance _root;

    public LumenPlayer(
        LmbMovieDefinition movie,
        float stageWidth,
        float stageHeight,
        uint? rootCharacterId = null)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));

        _movie = movie;
        StageWidth = stageWidth;
        StageHeight = stageHeight;
        _shapes = movie.Shapes.ToDictionary(shape => shape.CharacterId);
        _sprites = movie.Sprites.ToDictionary(sprite => sprite.CharacterId, compileTimeline);
        foreach (var timeline in _sprites.Values.Where(timeline => timeline.HasKeyFrames))
        {
            reportOnce(
                "LUM_KEYFRAMES_DEFERRED",
                timeline.CharacterId,
                0,
                "Key-frame interpolation data is retained but not applied by the display-list slice.");
        }

        var rootId = rootCharacterId ?? movie.Properties?.RootCharacterId
            ?? throw new ArgumentException("The movie has no candidate root sprite; provide a root character ID.", nameof(rootCharacterId));
        if (!_sprites.ContainsKey(rootId))
            throw new ArgumentException($"Root character {rootId} is not a defined sprite.", nameof(rootCharacterId));

        _root = new DisplayInstance(rootId);
        enterFrame(_root, 0);
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public int CurrentFrame => _root.Frame;

    public ImmutableArray<LumenRuntimeDiagnostic> Diagnostics => [.. _diagnostics];

    public void Advance()
    {
        var existing = new List<DisplayInstance>();
        collectPlayingInstances(_root, existing);
        foreach (var instance in existing)
        {
            if (!instance.Removed)
                advanceInstance(instance);
        }
    }

    public LumenRenderSnapshot CreateRenderSnapshot()
    {
        var quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
        appendInstance(_root, LumenMatrix.Identity, ColorState.Identity, quads);
        return new LumenRenderSnapshot(StageWidth, StageHeight, quads.ToImmutable());
    }

    private static SpriteTimeline compileTimeline(LmbSpriteDefinition sprite)
    {
        var frameCount = checked((int)sprite.DeclaredFrameCount);
        var builders = Enumerable.Range(0, frameCount)
            .Select(_ => ImmutableArray.CreateBuilder<LmbTimelineCommand>())
            .ToArray();
        int? ordinaryFrame = null;
        var hasKeyFrames = false;
        foreach (var command in sprite.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show when show.Frame < (uint)frameCount:
                    ordinaryFrame = (int)show.Frame;
                    break;
                case LmbFrameKeyCommand:
                    hasKeyFrames = true;
                    ordinaryFrame = null;
                    break;
                case LmbFrameLabelCommand:
                    break;
                default:
                    if (ordinaryFrame is int frame)
                        builders[frame].Add(command);
                    break;
            }
        }
        return new SpriteTimeline(
            sprite.CharacterId,
            builders.Select(builder => builder.ToImmutable()).ToImmutableArray(),
            hasKeyFrames);
    }

    private void advanceInstance(DisplayInstance instance)
    {
        var timeline = _sprites[instance.CharacterId];
        if (timeline.Frames.Length == 0)
            return;
        var nextFrame = instance.Frame + 1;
        if (nextFrame >= timeline.Frames.Length)
        {
            foreach (var child in instance.Children.Values)
                child.Removed = true;
            instance.Children.Clear();
            nextFrame = 0;
        }
        enterFrame(instance, nextFrame);
    }

    private void enterFrame(DisplayInstance instance, int frame)
    {
        var timeline = _sprites[instance.CharacterId];
        if ((uint)frame >= (uint)timeline.Frames.Length)
            return;
        instance.Frame = frame;
        foreach (var command in timeline.Frames[frame])
        {
            switch (command)
            {
                case LmbPlaceObjectCommand place:
                    applyPlacement(instance, place);
                    break;
                case LmbRemoveObjectCommand remove:
                    var depth = remove.CandidateDepth;
                    if (instance.Children.TryGetValue(depth, out var removed))
                    {
                        instance.Children.Remove(depth);
                        removed.Removed = true;
                    }
                    break;
                case LmbDoActionCommand:
                    reportOnce(
                        "LUM_ACTION_DEFERRED",
                        instance.CharacterId,
                        frame,
                        "AVM frame action is retained but not executed by the display-list slice.");
                    break;
            }
        }
    }

    private void applyPlacement(DisplayInstance parent, LmbPlaceObjectCommand placement)
    {
        parent.Children.TryGetValue(placement.Depth, out var instance);
        switch (placement.Mode)
        {
            case 1:
                if (instance is not null)
                    instance.Removed = true;
                instance = new DisplayInstance(placement.CharacterId)
                {
                    PlacementId = placement.PlacementId,
                };
                parent.Children[placement.Depth] = instance;
                applyPlacementFields(instance, placement, isNew: true);
                if (_sprites.ContainsKey(instance.CharacterId))
                    enterFrame(instance, 0);
                else if (!_shapes.ContainsKey(instance.CharacterId))
                    reportOnce("LUM_CHARACTER_NOT_FOUND", instance.CharacterId, parent.Frame, "Placed character has no shape or sprite definition.");
                break;
            case 2 when instance is not null:
                applyPlacementFields(instance, placement, isNew: false);
                break;
            case 3 when instance is not null:
                if (instance.CharacterId != placement.CharacterId)
                {
                    instance.Removed = true;
                    var replacement = new DisplayInstance(placement.CharacterId)
                    {
                        PlacementId = placement.PlacementId,
                        Transform = instance.Transform,
                        Color = instance.Color,
                        BlendMode = instance.BlendMode,
                    };
                    parent.Children[placement.Depth] = instance = replacement;
                    applyPlacementFields(instance, placement, isNew: false);
                    if (_sprites.ContainsKey(instance.CharacterId))
                        enterFrame(instance, 0);
                }
                else
                {
                    instance.PlacementId = placement.PlacementId;
                    applyPlacementFields(instance, placement, isNew: false);
                }
                break;
            default:
                reportOnce(
                    "LUM_UNSUPPORTED_PLACEMENT",
                    parent.CharacterId,
                    parent.Frame,
                    $"Placement mode {placement.Mode} at depth {placement.Depth} has no supported target state.");
                break;
        }
    }

    private void applyPlacementFields(DisplayInstance instance, LmbPlaceObjectCommand placement, bool isNew)
    {
        if (isNew || placement.BlendMode != 0)
            instance.BlendMode = placement.BlendMode;
        if (instance.BlendMode > 2)
        {
            reportOnce(
                "LUM_BLEND_MODE_DEFERRED",
                instance.CharacterId,
                0,
                $"Blend mode {instance.BlendMode} is currently rendered as normal.");
        }

        switch (placement.PositionKind)
        {
            case 0:
                if (placement.PositionIndex >= _movie.Matrices.Length)
                {
                    reportOnce("LUM_MATRIX_NOT_FOUND", instance.CharacterId, 0, $"Matrix {placement.PositionIndex} was left unchanged.");
                    break;
                }
                var matrix = _movie.Matrices[placement.PositionIndex];
                instance.Transform = new LumenMatrix(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.X, matrix.Y);
                break;
            case 0x8000:
                if (placement.PositionIndex >= _movie.Translations.Length)
                {
                    reportOnce("LUM_TRANSLATION_NOT_FOUND", instance.CharacterId, 0, $"Translation {placement.PositionIndex} was left unchanged.");
                    break;
                }
                var translation = _movie.Translations[placement.PositionIndex];
                instance.Transform = LumenMatrix.Identity with { X = translation.X, Y = translation.Y };
                break;
            case ushort.MaxValue:
                break;
            default:
                reportOnce(
                    "LUM_UNKNOWN_POSITION_KIND",
                    instance.CharacterId,
                    0,
                    $"Position kind 0x{placement.PositionKind:X4} was left unchanged.");
                break;
        }

        var color = instance.Color;
        if (placement.ColorMultiplyIndex < _movie.ColorTransforms.Length)
            color = color with { Multiply = convertColor(_movie.ColorTransforms[checked((int)placement.ColorMultiplyIndex)]) };
        else if (placement.ColorMultiplyIndex != uint.MaxValue)
            reportOnce("LUM_COLOR_NOT_FOUND", instance.CharacterId, 0, $"Multiply color {placement.ColorMultiplyIndex} was left unchanged.");
        else if (isNew)
            color = color with { Multiply = LumenRenderColor.White };
        if (placement.ColorAddIndex < _movie.ColorTransforms.Length)
            color = color with { Add = convertColor(_movie.ColorTransforms[checked((int)placement.ColorAddIndex)]) };
        else if (placement.ColorAddIndex != uint.MaxValue)
            reportOnce("LUM_COLOR_NOT_FOUND", instance.CharacterId, 0, $"Add color {placement.ColorAddIndex} was left unchanged.");
        else if (isNew)
            color = color with { Add = LumenRenderColor.Transparent };
        instance.Color = color;
    }

    private void appendInstance(
        DisplayInstance instance,
        LumenMatrix parentTransform,
        ColorState parentColor,
        ImmutableArray<LumenRenderQuad>.Builder quads)
    {
        if (instance.Removed)
            return;
        var transform = instance.Transform.Then(parentTransform);
        var color = instance.Color.Then(parentColor);
        if (_shapes.TryGetValue(instance.CharacterId, out var shape))
        {
            foreach (var geometry in shape.Geometry)
            {
                if (geometry.Vertices.Length != 4)
                {
                    reportOnce("LUM_NON_QUAD_GEOMETRY", instance.CharacterId, instance.Frame, "Only four-vertex shape geometry is renderable.");
                    continue;
                }
                if ((geometry.Flags >> 16) == 0)
                {
                    reportOnce("LUM_NATIVE_FILL_DEFERRED", instance.CharacterId, instance.Frame, "Fill-zero/native shape geometry has no host surface yet.");
                    continue;
                }

                quads.Add(new LumenRenderQuad(
                    geometry.TextureIndex,
                    transformVertex(geometry.Vertices[0], transform),
                    transformVertex(geometry.Vertices[1], transform),
                    transformVertex(geometry.Vertices[2], transform),
                    transformVertex(geometry.Vertices[3], transform),
                    color.Multiply,
                    color.Add));
            }
        }

        foreach (var child in instance.Children.Values)
            appendInstance(child, transform, color, quads);
    }

    private void reportOnce(string code, uint characterId, int frame, string message)
    {
        var key = $"{code}:{characterId}:{frame}:{message}";
        if (_diagnosticKeys.Add(key))
        {
            _diagnostics.Add(new LumenRuntimeDiagnostic(
                LumenRuntimeDiagnosticSeverity.Warning,
                code,
                characterId,
                frame,
                message));
        }
    }

    private static void collectPlayingInstances(DisplayInstance instance, List<DisplayInstance> destination)
    {
        if (instance.Removed)
            return;
        if (instance.Frame >= 0)
            destination.Add(instance);
        foreach (var child in instance.Children.Values)
            collectPlayingInstances(child, destination);
    }

    private static LumenRenderVertex transformVertex(LmbVertex vertex, LumenMatrix transform)
    {
        var position = transform.Transform(vertex.X, vertex.Y);
        return new LumenRenderVertex(position.X, position.Y, vertex.U, vertex.V);
    }

    private static LumenRenderColor convertColor(LmbColorTransform color) =>
        new(color.Red / 256f, color.Green / 256f, color.Blue / 256f, color.Alpha / 256f);

    private sealed class DisplayInstance(uint characterId)
    {
        public uint CharacterId { get; } = characterId;

        public uint PlacementId { get; set; } = uint.MaxValue;

        public int Frame { get; set; } = -1;

        public bool Removed { get; set; }

        public ushort BlendMode { get; set; }

        public LumenMatrix Transform { get; set; } = LumenMatrix.Identity;

        public ColorState Color { get; set; } = ColorState.Identity;

        public SortedDictionary<uint, DisplayInstance> Children { get; } = [];
    }

    private readonly record struct SpriteTimeline(
        uint CharacterId,
        ImmutableArray<ImmutableArray<LmbTimelineCommand>> Frames,
        bool HasKeyFrames);

    private readonly record struct ColorState(LumenRenderColor Multiply, LumenRenderColor Add)
    {
        public static ColorState Identity { get; } = new(LumenRenderColor.White, LumenRenderColor.Transparent);

        public ColorState Then(ColorState parent) =>
            new(
                multiply(Multiply, parent.Multiply),
                add(multiply(Add, parent.Multiply), parent.Add));

        private static LumenRenderColor multiply(LumenRenderColor left, LumenRenderColor right) =>
            new(
                left.Red * right.Red,
                left.Green * right.Green,
                left.Blue * right.Blue,
                left.Alpha * right.Alpha);

        private static LumenRenderColor add(LumenRenderColor left, LumenRenderColor right) =>
            new(
                left.Red + right.Red,
                left.Green + right.Green,
                left.Blue + right.Blue,
                left.Alpha + right.Alpha);
    }
}
