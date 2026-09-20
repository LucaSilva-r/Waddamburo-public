using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// Small deterministic display-list core. It currently applies ordinary-frame
/// placement/removal records, restores F105 seek snapshots, and builds interpolated
/// render snapshots; AVM actions remain explicit diagnostics until their runtime
/// module exists.
/// </summary>
public sealed class LumenPlayer
{
    private const float TranslationCutThreshold = 200f;
    private const float ColorCutThreshold = 0.3f;

    private readonly LmbMovieDefinition _movie;
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, SpriteTimeline> _sprites;
    private readonly List<LumenRuntimeDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly List<PendingFrameAction> _pendingActions = [];
    private readonly DisplayInstance _root;
    private long _nextActionSequence;

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
        _sprites = movie.Sprites.ToDictionary(
            sprite => sprite.CharacterId,
            sprite => compileTimeline(sprite, movie.Strings));
        var rootId = rootCharacterId ?? movie.Properties?.RootCharacterId
            ?? throw new ArgumentException("The movie has no candidate root sprite; provide a root character ID.", nameof(rootCharacterId));
        if (!_sprites.ContainsKey(rootId))
            throw new ArgumentException($"Root character {rootId} is not a defined sprite.", nameof(rootCharacterId));

        _root = new DisplayInstance(rootId, hierarchyDepth: 0);
        enterFrame(_root, 0, queueActions: true);
        drainActions();
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public int CurrentFrame => _root.Frame;

    public bool IsPlaying => _root.Playing;

    public ImmutableDictionary<string, int> Labels => _sprites[_root.CharacterId].Labels;

    public ImmutableArray<LumenRuntimeDiagnostic> Diagnostics => [.. _diagnostics];

    public void Advance()
    {
        snapshotInstances(_root);
        advanceSubtree(_root, queueActions: true);
        drainActions();
    }

    public void Seek(int frame)
    {
        var timeline = _sprites[_root.CharacterId];
        if ((uint)frame >= (uint)timeline.Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frame));

        seekCore(frame, _root.Playing);
    }

    public void GotoFrame(int frame, bool play)
    {
        var timeline = _sprites[_root.CharacterId];
        if ((uint)frame >= (uint)timeline.Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frame));

        if (frame == _root.Frame)
            _root.Playing = play;
        else
            seekCore(frame, play);
    }

    public void GotoLabel(string label, bool play)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var timeline = _sprites[_root.CharacterId];
        if (!timeline.Labels.TryGetValue(label, out var frame))
            throw new KeyNotFoundException($"Label '{label}' is not defined on root character {_root.CharacterId}.");
        GotoFrame(frame, play);
    }

    public void Play() => _root.Playing = true;

    public void Stop() => _root.Playing = false;

    private void seekCore(int frame, bool play)
    {
        _pendingActions.Clear();
        _root.Playing = true;
        restoreInstance(_root, frame);
        _root.Playing = play;
        enqueueCurrentFrameActions(_root);
        drainActions();
        resetInterpolation(_root);
    }

    private void advanceSubtree(DisplayInstance root, bool queueActions)
    {
        var existing = new List<DisplayInstance>();
        collectPlayingInstances(root, existing);
        foreach (var instance in existing)
        {
            if (!instance.Removed)
                advanceInstance(instance, queueActions);
        }
    }

    public LumenRenderSnapshot CreateRenderSnapshot(float interpolationFraction = 1f)
    {
        if (!float.IsFinite(interpolationFraction) || interpolationFraction < 0 || interpolationFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(interpolationFraction));
        var quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
        appendInstance(_root, LumenMatrix.Identity, ColorState.Identity, interpolationFraction, quads);
        return new LumenRenderSnapshot(StageWidth, StageHeight, quads.ToImmutable());
    }

    private static SpriteTimeline compileTimeline(
        LmbSpriteDefinition sprite,
        ImmutableArray<LmbString> strings)
    {
        var frameCount = checked((int)sprite.DeclaredFrameCount);
        var builders = Enumerable.Range(0, frameCount)
            .Select(_ => ImmutableArray.CreateBuilder<LmbTimelineCommand>())
            .ToArray();
        int? ordinaryFrame = null;
        int? keyFrame = null;
        var keyBuilders = new Dictionary<int, ImmutableArray<LmbTimelineCommand>.Builder>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var command in sprite.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show when show.Frame < (uint)frameCount:
                    ordinaryFrame = (int)show.Frame;
                    keyFrame = null;
                    break;
                case LmbFrameKeyCommand key when key.Frame < (uint)frameCount:
                    ordinaryFrame = null;
                    keyFrame = (int)key.Frame;
                    if (!keyBuilders.ContainsKey(keyFrame.Value))
                        keyBuilders.Add(keyFrame.Value, ImmutableArray.CreateBuilder<LmbTimelineCommand>());
                    break;
                case LmbFrameLabelCommand label
                    when label.StringIndex < (uint)strings.Length
                        && label.Frame < (uint)frameCount:
                    labels[strings[checked((int)label.StringIndex)].Value] = checked((int)label.Frame);
                    break;
                default:
                    if (ordinaryFrame is int frame)
                        builders[frame].Add(command);
                    else if (keyFrame is int key)
                        keyBuilders[key].Add(command);
                    break;
            }
        }
        return new SpriteTimeline(
            sprite.CharacterId,
            builders.Select(builder => builder.ToImmutable()).ToImmutableArray(),
            keyBuilders.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable()),
            labels.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private void restoreInstance(DisplayInstance instance, int targetFrame)
    {
        var timeline = _sprites[instance.CharacterId];
        if ((uint)targetFrame >= (uint)timeline.Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(targetFrame));

        var keyFrame = timeline.KeyFrames.Keys
            .Where(frame => frame <= targetFrame)
            .DefaultIfEmpty(-1)
            .Max();
        clearChildren(instance);
        if (keyFrame >= 0)
        {
            instance.Frame = keyFrame;
            applyCommands(instance, timeline.KeyFrames[keyFrame], queueActions: false);
            foreach (var child in instance.Children.Values)
            {
                if (!_sprites.TryGetValue(child.CharacterId, out var childTimeline)
                    || childTimeline.Frames.IsEmpty)
                {
                    continue;
                }
                var age = Math.Max(0, keyFrame - child.FirstFrame);
                restoreInstance(child, age % childTimeline.Frames.Length);
            }
        }
        else
        {
            instance.Frame = -1;
            enterFrame(instance, 0, queueActions: false);
            keyFrame = 0;
        }

        for (var frame = keyFrame + 1; frame <= targetFrame; frame++)
            advanceSubtree(instance, queueActions: false);
    }

    private void advanceInstance(DisplayInstance instance, bool queueActions)
    {
        if (!instance.Playing)
            return;
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
        enterFrame(instance, nextFrame, queueActions);
    }

    private void enterFrame(DisplayInstance instance, int frame, bool queueActions)
    {
        var timeline = _sprites[instance.CharacterId];
        if ((uint)frame >= (uint)timeline.Frames.Length)
            return;
        instance.Frame = frame;
        applyCommands(instance, timeline.Frames[frame], queueActions);
    }

    private void applyCommands(
        DisplayInstance instance,
        ImmutableArray<LmbTimelineCommand> commands,
        bool queueActions)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case LmbPlaceObjectCommand place:
                    applyPlacement(instance, place, queueActions);
                    break;
                case LmbRemoveObjectCommand remove:
                    var depth = remove.Depth;
                    if (instance.Children.TryGetValue(depth, out var removed))
                    {
                        instance.Children.Remove(depth);
                        removed.Removed = true;
                    }
                    break;
                case LmbDoActionCommand action when queueActions:
                    _pendingActions.Add(new PendingFrameAction(
                        instance,
                        action.ActionIndex,
                        _nextActionSequence++));
                    break;
            }
        }
    }

    private void enqueueCurrentFrameActions(DisplayInstance instance)
    {
        if (instance.Removed || instance.Frame < 0)
            return;
        var timeline = _sprites[instance.CharacterId];
        foreach (var action in timeline.Frames[instance.Frame].OfType<LmbDoActionCommand>())
        {
            _pendingActions.Add(new PendingFrameAction(
                instance,
                action.ActionIndex,
                _nextActionSequence++));
        }
        foreach (var child in instance.Children.Values)
        {
            if (_sprites.ContainsKey(child.CharacterId))
                enqueueCurrentFrameActions(child);
        }
    }

    private void drainActions()
    {
        foreach (var pending in _pendingActions
            .OrderBy(action => action.Instance.HierarchyDepth)
            .ThenBy(action => action.Sequence))
        {
            if (pending.Instance.Removed)
                continue;
            if (pending.ActionIndex >= (uint)_movie.Actions.Length
                || !tryExecuteSimpleAction(
                    pending.Instance,
                    _movie.Actions[checked((int)pending.ActionIndex)].Code))
            {
                reportOnce(
                    "LUM_ACTION_DEFERRED",
                    pending.Instance.CharacterId,
                    pending.Instance.Frame,
                    $"AVM action {pending.ActionIndex} requires the full interpreter.");
            }
        }
        _pendingActions.Clear();
    }

    private static bool tryExecuteSimpleAction(
        DisplayInstance instance,
        Avm1CodeBlock code)
    {
        var controls = new List<byte>();
        var foundEnd = false;
        foreach (var instruction in code.Instructions)
        {
            var opcode = instruction.Opcode;
            if (opcode == 0)
            {
                foundEnd = true;
                break;
            }
            if (opcode is not (0x06 or 0x07))
                return false;
            controls.Add(opcode);
        }

        if (!foundEnd)
            return false;

        foreach (var opcode in controls)
            instance.Playing = opcode == 0x06;
        return true;
    }

    private void applyPlacement(
        DisplayInstance parent,
        LmbPlaceObjectCommand placement,
        bool queueActions)
    {
        parent.Children.TryGetValue(placement.Depth, out var instance);
        switch (placement.Mode)
        {
            case 1:
                if (instance is not null)
                    instance.Removed = true;
                instance = new DisplayInstance(placement.CharacterId, parent.HierarchyDepth + 1)
                {
                    PlacementId = placement.PlacementId,
                    FirstFrame = placement.FirstFrame,
                };
                parent.Children[placement.Depth] = instance;
                applyPlacementFields(instance, placement, isNew: true);
                if (_sprites.ContainsKey(instance.CharacterId))
                    enterFrame(instance, 0, queueActions);
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
                    var replacement = new DisplayInstance(placement.CharacterId, parent.HierarchyDepth + 1)
                    {
                        PlacementId = placement.PlacementId,
                        FirstFrame = placement.FirstFrame,
                        Transform = instance.Transform,
                        Color = instance.Color,
                        BlendMode = instance.BlendMode,
                    };
                    parent.Children[placement.Depth] = instance = replacement;
                    applyPlacementFields(instance, placement, isNew: false);
                    if (_sprites.ContainsKey(instance.CharacterId))
                        enterFrame(instance, 0, queueActions);
                }
                else
                {
                    instance.PlacementId = placement.PlacementId;
                    instance.FirstFrame = placement.FirstFrame;
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
        float interpolationFraction,
        ImmutableArray<LumenRenderQuad>.Builder quads)
    {
        if (instance.Removed)
            return;
        var local = interpolate(instance, interpolationFraction);
        var transform = local.Transform.Then(parentTransform);
        var color = local.Color.Then(parentColor);
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
            appendInstance(child, transform, color, interpolationFraction, quads);
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

    private static void snapshotInstances(DisplayInstance instance)
    {
        if (instance.Removed)
            return;
        instance.PreviousTransform = instance.Transform;
        instance.PreviousMultiply = instance.Color.Multiply;
        foreach (var child in instance.Children.Values)
            snapshotInstances(child);
    }

    private static void resetInterpolation(DisplayInstance instance)
    {
        if (instance.Removed)
            return;
        instance.PreviousTransform = instance.Transform;
        instance.PreviousMultiply = instance.Color.Multiply;
        foreach (var child in instance.Children.Values)
            resetInterpolation(child);
    }

    private static void clearChildren(DisplayInstance instance)
    {
        foreach (var child in instance.Children.Values)
            child.Removed = true;
        instance.Children.Clear();
    }

    private static InterpolatedState interpolate(DisplayInstance instance, float fraction)
    {
        if (fraction >= 1 || instance.PreviousTransform is not LumenMatrix previousTransform
            || instance.PreviousMultiply is not LumenRenderColor previousMultiply)
        {
            return new InterpolatedState(instance.Transform, instance.Color);
        }

        var currentTransform = instance.Transform;
        if (Math.Abs(currentTransform.X - previousTransform.X)
            + Math.Abs(currentTransform.Y - previousTransform.Y) > TranslationCutThreshold
            || colorDistanceExceeds(previousMultiply, instance.Color.Multiply, ColorCutThreshold))
        {
            return new InterpolatedState(instance.Transform, instance.Color);
        }

        return new InterpolatedState(
            new LumenMatrix(
                lerp(previousTransform.M11, currentTransform.M11, fraction),
                lerp(previousTransform.M12, currentTransform.M12, fraction),
                lerp(previousTransform.M21, currentTransform.M21, fraction),
                lerp(previousTransform.M22, currentTransform.M22, fraction),
                lerp(previousTransform.X, currentTransform.X, fraction),
                lerp(previousTransform.Y, currentTransform.Y, fraction)),
            instance.Color with
            {
                Multiply = new LumenRenderColor(
                    lerp(previousMultiply.Red, instance.Color.Multiply.Red, fraction),
                    lerp(previousMultiply.Green, instance.Color.Multiply.Green, fraction),
                    lerp(previousMultiply.Blue, instance.Color.Multiply.Blue, fraction),
                    lerp(previousMultiply.Alpha, instance.Color.Multiply.Alpha, fraction)),
            });
    }

    private static bool colorDistanceExceeds(
        LumenRenderColor previous,
        LumenRenderColor current,
        float threshold) =>
        Math.Abs(current.Red - previous.Red) > threshold
        || Math.Abs(current.Green - previous.Green) > threshold
        || Math.Abs(current.Blue - previous.Blue) > threshold
        || Math.Abs(current.Alpha - previous.Alpha) > threshold;

    private static float lerp(float from, float to, float fraction) =>
        from + ((to - from) * fraction);

    private static LumenRenderVertex transformVertex(LmbVertex vertex, LumenMatrix transform)
    {
        var position = transform.Transform(vertex.X, vertex.Y);
        return new LumenRenderVertex(position.X, position.Y, vertex.U, vertex.V);
    }

    private static LumenRenderColor convertColor(LmbColorTransform color) =>
        new(color.Red / 256f, color.Green / 256f, color.Blue / 256f, color.Alpha / 256f);

    private sealed class DisplayInstance(uint characterId, int hierarchyDepth)
    {
        public uint CharacterId { get; } = characterId;

        public int HierarchyDepth { get; } = hierarchyDepth;

        public uint PlacementId { get; set; } = uint.MaxValue;

        public int FirstFrame { get; set; }

        public int Frame { get; set; } = -1;

        public bool Removed { get; set; }

        public bool Playing { get; set; } = true;

        public ushort BlendMode { get; set; }

        public LumenMatrix Transform { get; set; } = LumenMatrix.Identity;

        public ColorState Color { get; set; } = ColorState.Identity;

        public LumenMatrix? PreviousTransform { get; set; }

        public LumenRenderColor? PreviousMultiply { get; set; }

        public SortedDictionary<uint, DisplayInstance> Children { get; } = [];
    }

    private readonly record struct SpriteTimeline(
        uint CharacterId,
        ImmutableArray<ImmutableArray<LmbTimelineCommand>> Frames,
        ImmutableDictionary<int, ImmutableArray<LmbTimelineCommand>> KeyFrames,
        ImmutableDictionary<string, int> Labels);

    private readonly record struct InterpolatedState(LumenMatrix Transform, ColorState Color);

    private readonly record struct PendingFrameAction(
        DisplayInstance Instance,
        uint ActionIndex,
        long Sequence);

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
