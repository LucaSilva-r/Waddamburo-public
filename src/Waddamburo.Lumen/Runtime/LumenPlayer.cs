using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// Small deterministic display-list core. It currently applies ordinary-frame
/// placement/removal records, restores F105 seek snapshots, and builds interpolated
/// render snapshots, and executes the bounded AVM subset used by initialized
/// timelines.
/// </summary>
public sealed class LumenPlayer
{
    private const float TranslationCutThreshold = 200f;
    private const float ColorCutThreshold = 0.3f;

    private readonly LmbMovieDefinition _movie;
    private readonly LumenRuntimeLimits _limits;
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, SpriteTimeline> _sprites;
    private readonly List<LumenRuntimeDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly List<PendingFrameAction> _pendingActions = [];
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Avm1FunctionValue> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallbackRegistration> _callbacks = new(StringComparer.Ordinal);
    private readonly Avm1Object _externalInterface = new();
    private readonly DisplayInstance _root;
    private LumenInputSnapshot _inputSnapshot = LumenInputSnapshot.Empty;
    private long _nextActionSequence;
    private int _callDepth;

    public LumenPlayer(
        LmbMovieDefinition movie,
        float stageWidth,
        float stageHeight,
        uint? rootCharacterId = null,
        LumenRuntimeLimits? limits = null,
        ILumenHostBinding? hostBinding = null)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));

        _movie = movie;
        _limits = limits ?? LumenRuntimeLimits.Default;
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

        _root = new DisplayInstance(rootId, hierarchyDepth: 0, parent: null) { Name = "_level0" };
        _globals["_global"] = _globals;
        _globals["_root"] = _root;
        _globals["Object"] = createBuiltinConstructor();
        _globals["MovieClip"] = createBuiltinConstructor();
        _globals["Array"] = createBuiltinConstructor();
        installKeyObject();
        installExternalInterface();
        hostBinding?.Install(new LumenHostContext(_globals));
        enterFrame(_root, 0, queueActions: true);
        drainActions();
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public int CurrentFrame => _root.Frame;

    public bool IsPlaying => _root.Playing;

    public ImmutableDictionary<string, int> Labels => _sprites[_root.CharacterId].Labels;

    public ImmutableArray<LumenRuntimeDiagnostic> Diagnostics => [.. _diagnostics];

    public ImmutableArray<string> CallbackNames => [.. _callbacks.Keys.Order(StringComparer.Ordinal)];

    public bool TryInvokeCallback(string name, IReadOnlyList<LumenHostValue> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_callbacks.TryGetValue(name, out var callback))
            return false;
        var converted = arguments.Select(fromHostValue).ToArray();
        return invokeFunction(
            callback.Instance,
            callback.Function,
            callback.ThisValue,
            converted).Found;
    }

    public void Advance()
        => Advance(LumenInputSnapshot.Empty);

    public void Advance(LumenInputSnapshot inputSnapshot)
    {
        ArgumentNullException.ThrowIfNull(inputSnapshot);
        _inputSnapshot = inputSnapshot;
        snapshotInstances(_root);
        advanceSubtree(_root, queueActions: true);
        drainActions();
        dispatchEnterFrameHandlers();
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
        appendInstance(
            _root,
            LumenMatrix.Identity,
            ColorState.Identity,
            LumenRenderBlend.Normal,
            interpolationFraction,
            quads);
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
            sprite.ExportStringIndex is uint exportIndex && exportIndex < strings.Length
                ? strings[checked((int)exportIndex)].Value
                : null,
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
                    enqueueAction(instance, action.ActionIndex);
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
            enqueueAction(instance, action.ActionIndex);
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
            if (pending.ActionIndex >= (uint)_movie.Actions.Length)
            {
                reportOnce(
                    "LUM_ACTION_DEFERRED",
                    pending.Instance.CharacterId,
                    pending.Instance.Frame,
                    $"AVM action {pending.ActionIndex} requires the full interpreter.");
                continue;
            }

            var status = executeAction(
                pending.Instance,
                _movie.Actions[checked((int)pending.ActionIndex)].Code);
            if (status == Avm1ExecutionStatus.Success)
                continue;
            var limited = status is Avm1ExecutionStatus.InstructionLimit
                or Avm1ExecutionStatus.StackLimit
                or Avm1ExecutionStatus.RegisterLimit;
            reportOnce(
                limited ? "LUM_ACTION_LIMIT" : "LUM_ACTION_DEFERRED",
                pending.Instance.CharacterId,
                pending.Instance.Frame,
                limited
                    ? $"AVM action {pending.ActionIndex} reached the {status} safety limit."
                    : $"AVM action {pending.ActionIndex} requires unsupported semantics or failed with {status}.");
        }
        _pendingActions.Clear();
    }

    private void dispatchEnterFrameHandlers()
    {
        var instances = new List<DisplayInstance>();
        collectInstancesPostOrder(_root, instances);
        if (instances.Count > _limits.MaxPendingActions)
        {
            reportOnce(
                "LUM_EVENT_QUEUE_LIMIT",
                _root.CharacterId,
                _root.Frame,
                $"Enter-frame candidate count {instances.Count} exceeds the {_limits.MaxPendingActions}-event limit.");
            instances.RemoveRange(_limits.MaxPendingActions, instances.Count - _limits.MaxPendingActions);
        }
        foreach (var instance in instances)
        {
            if (instance.Removed)
                continue;
            var handler = readMember(instance, "onEnterFrame");
            if (handler.Found && handler.Value is Avm1FunctionValue function)
                invokeFunction(instance, function, instance, []);
        }
    }

    private void enqueueAction(DisplayInstance instance, uint actionIndex)
    {
        if (_pendingActions.Count >= _limits.MaxPendingActions)
        {
            reportOnce(
                "LUM_ACTION_QUEUE_LIMIT",
                instance.CharacterId,
                instance.Frame,
                $"Pending AVM action limit {_limits.MaxPendingActions} was reached.");
            return;
        }
        _pendingActions.Add(new PendingFrameAction(instance, actionIndex, _nextActionSequence++));
    }

    private Avm1ExecutionStatus executeAction(
        DisplayInstance instance,
        Avm1CodeBlock code)
    {
        var context = createExecutionContext(instance);
        var status = Avm1Interpreter.TryExecute(
            code,
            _movie.Strings,
            instance.Playing,
            _limits,
            context,
            out var playing);
        if (status == Avm1ExecutionStatus.Success)
        {
            instance.Playing = playing;
            context.Commit();
        }
        return status;
    }

    private Avm1ExecutionContext createExecutionContext(
        DisplayInstance instance,
        Avm1ExecutionContext? parentContext = null)
    {
        Avm1ExecutionContext? context = null;
        context = new Avm1ExecutionContext(
            name =>
            {
                if (parentContext is not null)
                    return parentContext.GetVariable(name);
                if (name == "this")
                    return new Avm1Lookup(true, instance);
                if (name == "_root" || name == "_level0")
                    return new Avm1Lookup(true, _root);
                if (name == "_parent")
                    return new Avm1Lookup(instance.Parent is not null, instance.Parent);
                if (name == "_global")
                    return new Avm1Lookup(true, _globals);
                var instanceMember = context!.GetMember(instance, name);
                return instanceMember.Found
                    ? instanceMember
                    : context.GetMember(_globals, name);
            },
            (name, value) =>
            {
                if (parentContext is null)
                    instance.Variables[name] = value;
                else
                    parentContext.SetVariable(name, value);
            },
            (target, name) => parentContext?.GetMember(target, name) ?? readMember(target, name),
            (target, name, value) =>
            {
                if (parentContext is null)
                    writeMember(target, name, value);
                else
                    parentContext.SetMember(target, name, value);
            },
            (name, arguments) =>
            {
                var candidate = context!.GetVariable(name);
                if (candidate.Found && candidate.Value is Avm1FunctionValue function)
                    return invokeFunction(instance, function, instance, arguments, context);
                if (candidate.Found && candidate.Value is Avm1NativeFunction nativeFunction)
                    return invokeHost(instance, nativeFunction, arguments);
                if (name == "ASSetPropFlags")
                    return new Avm1Lookup(true, Avm1Undefined.Instance);
                reportOnce(
                    "LUM_AVM_CALL_UNRESOLVED",
                    instance.CharacterId,
                    instance.Frame,
                    $"AVM function '{name}' is not registered; undefined was returned.");
                return default;
            },
            (target, name, arguments, instructionOffset) =>
            {
                if (ReferenceEquals(target, _externalInterface) && name == "addCallback")
                {
                    if (arguments.Count >= 3
                        && arguments[0] is string callbackName
                        && arguments[2] is Avm1FunctionValue callbackFunction)
                    {
                        context!.OnCommit(() => _callbacks[callbackName] = new CallbackRegistration(
                            instance,
                            arguments[1],
                            callbackFunction));
                        return new Avm1Lookup(true, true);
                    }
                    return new Avm1Lookup(true, false);
                }
                if (ReferenceEquals(target, _externalInterface) && name == "call")
                    return new Avm1Lookup(true, Avm1Undefined.Instance);
                if (name == "toString" && target is null or bool or double or string or Avm1Undefined)
                    return new Avm1Lookup(true, toAvmString(target));
                if (target is string text && name == "charAt")
                {
                    var index = arguments.Count > 0 ? (int)toNumber(arguments[0]) : 0;
                    return new Avm1Lookup(
                        true,
                        (uint)index < (uint)text.Length ? text[index].ToString() : string.Empty);
                }
                if (target is string codeText && name == "charCodeAt")
                {
                    var index = arguments.Count > 0 ? (int)toNumber(arguments[0]) : 0;
                    return new Avm1Lookup(
                        true,
                        (uint)index < (uint)codeText.Length ? (double)codeText[index] : double.NaN);
                }
                if (target is DisplayInstance targetInstance && name is "play" or "stop")
                {
                    context!.OnCommit(() => targetInstance.Playing = name == "play");
                    return new Avm1Lookup(true, Avm1Undefined.Instance);
                }
                if (target is DisplayInstance jumpTarget
                    && name is "gotoAndPlay" or "gotoAndStop"
                    && arguments.Count > 0)
                {
                    if (tryResolveTimelineFrame(jumpTarget, arguments[0], out var targetFrame))
                        context!.OnCommit(() => jumpInstance(jumpTarget, targetFrame, name == "gotoAndPlay"));
                    else
                        reportOnce(
                            "LUM_AVM_GOTO_INVALID",
                            instance.CharacterId,
                            instance.Frame,
                            $"Movie clip method '{name}' could not resolve target '{toAvmString(arguments[0])}'.");
                    return new Avm1Lookup(true, Avm1Undefined.Instance);
                }
                if (name.Length == 0 && target is Avm1Object)
                    return new Avm1Lookup(true, Avm1Undefined.Instance);
                if (name == "registerClass"
                    && ReferenceEquals(target, _globals["Object"])
                    && arguments.Count >= 2
                    && arguments[0] is string exportName
                    && arguments[1] is Avm1FunctionValue registeredClass)
                {
                    context!.OnCommit(() => registerClass(exportName, registeredClass));
                    return new Avm1Lookup(true, true);
                }
                var candidate = name.Length == 0
                    ? new Avm1Lookup(target is Avm1FunctionValue, target)
                    : context!.GetMember(target, name);
                if (candidate.Found && candidate.Value is Avm1FunctionValue function)
                    return invokeFunction(instance, function, target, arguments, context);
                if (candidate.Found && candidate.Value is Avm1NativeFunction nativeFunction)
                    return invokeHost(instance, nativeFunction, arguments);
                reportOnce(
                    "LUM_AVM_METHOD_UNRESOLVED",
                    instance.CharacterId,
                    instance.Frame,
                    $"AVM method '{name}' at action offset 0x{instructionOffset:X} is not registered on {describeAvmType(target)}; undefined was returned.");
                return default;
            },
            (name, arguments) =>
            {
                var candidate = context!.GetVariable(name);
                var value = new Avm1Object();
                value.Properties["__constructor__"] = name;
                value.Properties["__arguments__"] = new Avm1ArrayObject(arguments);
                if (candidate.Found && candidate.Value is Avm1FunctionValue constructor)
                {
                    value.Prototype = constructor.GetProperty("prototype").Value as Avm1Object;
                    if (!invokeFunction(instance, constructor, value, arguments, context).Found)
                        return default;
                }
                return new Avm1Lookup(true, value);
            },
            parentContext is null ? null : parentContext.OnCommit);
        return context;
    }

    private Avm1Lookup invokeHost(
        DisplayInstance instance,
        Avm1NativeFunction function,
        IReadOnlyList<object?> arguments)
    {
        try
        {
            var converted = arguments.Select(toHostValue).ToImmutableArray();
            return new Avm1Lookup(true, fromHostValue(function.Callback(new LumenHostCall(converted))));
        }
        catch (Exception exception)
        {
            reportOnce(
                "LUM_HOST_CALL_FAILED",
                instance.CharacterId,
                instance.Frame,
                $"Host call '{function.Name}' failed with {exception.GetType().Name}; undefined was returned.");
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        }
    }

    private static LumenHostValue toHostValue(object? value) => value switch
    {
        null => LumenHostValue.Null,
        Avm1Undefined => LumenHostValue.Undefined,
        bool boolean => LumenHostValue.FromBoolean(boolean),
        double number => LumenHostValue.FromNumber(number),
        string text => LumenHostValue.FromString(text),
        _ => LumenHostValue.Undefined,
    };

    private static object? fromHostValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => Avm1Undefined.Instance,
        LumenHostValueKind.Null => null,
        LumenHostValueKind.Boolean => value.AsBoolean(),
        LumenHostValueKind.Number => value.AsNumber(),
        LumenHostValueKind.Text => value.AsString(),
        _ => Avm1Undefined.Instance,
    };

    private Avm1Lookup invokeFunction(
        DisplayInstance timelineTarget,
        Avm1FunctionValue function,
        object? thisValue,
        IReadOnlyList<object?> arguments,
        Avm1ExecutionContext? parentContext = null)
    {
        if (_callDepth >= _limits.MaxCallDepth)
        {
            reportOnce(
                "LUM_AVM_CALL_LIMIT",
                timelineTarget.CharacterId,
                timelineTarget.Frame,
                $"AVM call depth limit {_limits.MaxCallDepth} was reached.");
            return default;
        }
        _callDepth++;
        try
        {
            var context = createExecutionContext(timelineTarget, parentContext);
            var status = Avm1Interpreter.TryExecuteFunction(
                function,
                _movie.Strings,
                thisValue,
                arguments,
                timelineTarget.Playing,
                _limits,
                context,
                out var playing,
                out var returnValue);
            if (status != Avm1ExecutionStatus.Success)
            {
                reportOnce(
                    "LUM_AVM_FUNCTION_DEFERRED",
                    timelineTarget.CharacterId,
                    timelineTarget.Frame,
                    $"AVM function body requires unsupported semantics or failed with {status}.");
                return default;
            }
            timelineTarget.Playing = playing;
            context.Commit();
            return new Avm1Lookup(true, returnValue);
        }
        finally
        {
            _callDepth--;
        }
    }

    private void registerClass(string exportName, Avm1FunctionValue function)
    {
        _classes[exportName] = function;
        bindClass(_root, exportName, function);
    }

    private void bindClass(DisplayInstance instance, string exportName, Avm1FunctionValue function)
    {
        if (_sprites.TryGetValue(instance.CharacterId, out var timeline)
            && timeline.ExportName == exportName
            && !ReferenceEquals(instance.ConstructedClass, function))
        {
            constructInstance(instance, function);
        }
        foreach (var child in instance.Children.Values)
            bindClass(child, exportName, function);
    }

    private void constructInstance(DisplayInstance instance, Avm1FunctionValue function)
    {
        instance.ScriptPrototype = function.GetProperty("prototype").Value as Avm1Object;
        var result = invokeFunction(instance, function, instance, []);
        if (result.Found)
            instance.ConstructedClass = function;
    }

    private bool tryResolveTimelineFrame(DisplayInstance instance, object? value, out int frame)
    {
        var timeline = _sprites[instance.CharacterId];
        if (value is string label && timeline.Labels.TryGetValue(label, out frame))
            return true;
        var authoredFrame = toNumber(value);
        if (double.IsFinite(authoredFrame)
            && authoredFrame >= 1
            && authoredFrame <= timeline.Frames.Length)
        {
            frame = checked((int)Math.Truncate(authoredFrame)) - 1;
            return true;
        }
        frame = -1;
        return false;
    }

    private void jumpInstance(DisplayInstance instance, int frame, bool play)
    {
        if (instance.Removed)
            return;
        if (instance.Frame != frame)
        {
            instance.Playing = true;
            if (frame > instance.Frame)
            {
                for (var nextFrame = instance.Frame + 1; nextFrame <= frame; nextFrame++)
                    enterFrame(instance, nextFrame, queueActions: false);
            }
            else
                restoreInstance(instance, frame);
            enqueueCurrentFrameActions(instance);
            resetInterpolation(instance);
        }
        instance.Playing = play;
    }

    private static Avm1Lookup readMember(object? target, string name)
    {
        if (target is DisplayInstance instance)
        {
            var child = instance.Children.Values.FirstOrDefault(candidate => candidate.Name == name && !candidate.Removed);
            if (child is not null)
                return new Avm1Lookup(true, child);
            if (instance.Variables.TryGetValue(name, out var value))
                return new Avm1Lookup(true, value);
            if (instance.ScriptPrototype?.GetProperty(name) is { Found: true } inherited)
                return inherited;
            return name switch
            {
                "_visible" => new Avm1Lookup(true, instance.Visible),
                "_x" => new Avm1Lookup(true, (double)instance.Transform.X),
                "_y" => new Avm1Lookup(true, (double)instance.Transform.Y),
                "_alpha" => new Avm1Lookup(true, instance.Color.Multiply.Alpha * 100d),
                "_currentframe" => new Avm1Lookup(true, (double)instance.Frame + 1),
                _ => default,
            };
        }
        if (target is Avm1ArrayObject array)
        {
            if (array.Properties.TryGetValue(name, out var overridden))
                return new Avm1Lookup(true, overridden);
            var indexed = array.GetIndexedProperty(name);
            if (indexed.Found)
                return indexed;
        }
        if (target is string text && name == "length")
            return new Avm1Lookup(true, (double)text.Length);
        if (target is Avm1Object avmObject)
            return avmObject.GetProperty(name);
        if (target is Dictionary<string, object?> dictionary && dictionary.TryGetValue(name, out var member))
            return new Avm1Lookup(true, member);
        return default;
    }

    private static void writeMember(object? target, string name, object? value)
    {
        if (target is DisplayInstance instance)
        {
            switch (name)
            {
                case "_visible":
                    instance.Visible = toBoolean(value);
                    return;
                case "_x":
                    instance.Transform = instance.Transform with { X = (float)toNumber(value) };
                    return;
                case "_y":
                    instance.Transform = instance.Transform with { Y = (float)toNumber(value) };
                    return;
                case "_alpha":
                    instance.Color = instance.Color with
                    {
                        Multiply = instance.Color.Multiply with { Alpha = (float)(toNumber(value) / 100d) },
                    };
                    return;
                default:
                    instance.Variables[name] = value;
                    return;
            }
        }
        if (target is Avm1Object avmObject)
        {
            if (name == "__proto__" && value is Avm1Object prototype)
                avmObject.Prototype = prototype;
            else
                avmObject.Properties[name] = value;
            return;
        }
        if (target is Dictionary<string, object?> dictionary)
            dictionary[name] = value;
    }

    private static Avm1Object createBuiltinConstructor()
    {
        var constructor = new Avm1Object();
        constructor.Properties["prototype"] = new Avm1Object();
        return constructor;
    }

    private void installExternalInterface()
    {
        _externalInterface.Properties["available"] = true;
        var external = new Avm1Object();
        external.Properties["ExternalInterface"] = _externalInterface;
        var flash = new Avm1Object();
        flash.Properties["external"] = external;
        _globals["flash"] = flash;
    }

    private void installKeyObject()
    {
        var key = new Avm1Object();
        key.Properties["isDown"] = new Avm1NativeFunction(
            "Key.isDown",
            call =>
            {
                if (call.Arguments.IsEmpty || call.Arguments[0].Kind != LumenHostValueKind.Number)
                    return LumenHostValue.FromBoolean(false);
                var code = call.Arguments[0].AsNumber();
                return LumenHostValue.FromBoolean(
                    double.IsFinite(code)
                    && code >= int.MinValue
                    && code <= int.MaxValue
                    && _inputSnapshot.IsDown((int)code));
            });
        _globals["Key"] = key;
    }

    private static bool toBoolean(object? value) => value switch
    {
        null or Avm1Undefined => false,
        bool boolean => boolean,
        double number => number != 0 && !double.IsNaN(number),
        string text => text.Length != 0,
        _ => true,
    };

    private static double toNumber(object? value) => value switch
    {
        null => 0,
        Avm1Undefined => double.NaN,
        bool boolean => boolean ? 1 : 0,
        double number => number,
        string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        _ => double.NaN,
    };

    private static string toAvmString(object? value) => value switch
    {
        null => "null",
        Avm1Undefined => "undefined",
        bool boolean => boolean ? "true" : "false",
        double number => number.ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        string text => text,
        _ => "[object Object]",
    };

    private static string describeAvmType(object? value) => value switch
    {
        null => "null",
        Avm1Undefined => "undefined",
        DisplayInstance => "a movie clip",
        Avm1FunctionValue => "a function",
        Avm1ArrayObject => "an array",
        Avm1Object => "an object",
        Dictionary<string, object?> => "a global object",
        bool => "a boolean",
        double => "a number",
        string => "a string",
        _ => "an unknown value",
    };

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
                instance = new DisplayInstance(placement.CharacterId, parent.HierarchyDepth + 1, parent)
                {
                    PlacementId = placement.PlacementId,
                    FirstFrame = placement.FirstFrame,
                };
                parent.Children[placement.Depth] = instance;
                applyPlacementFields(instance, placement, isNew: true);
                if (_sprites.TryGetValue(instance.CharacterId, out var childTimeline))
                {
                    enterFrame(instance, 0, queueActions);
                    var exportName = childTimeline.ExportName;
                    if (exportName is not null && _classes.TryGetValue(exportName, out var registeredClass))
                        constructInstance(instance, registeredClass);
                }
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
                    var replacement = new DisplayInstance(placement.CharacterId, parent.HierarchyDepth + 1, parent)
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
        if (placement.NameStringIndex != 0 && placement.NameStringIndex < _movie.Strings.Length)
            instance.Name = _movie.Strings[checked((int)placement.NameStringIndex)].Value;
        if (isNew || placement.BlendMode != 0)
            instance.BlendMode = placement.BlendMode;
        if (instance.BlendMode > 2 && instance.BlendMode != 8)
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
        LumenRenderBlend parentBlend,
        float interpolationFraction,
        ImmutableArray<LumenRenderQuad>.Builder quads)
    {
        if (instance.Removed || !instance.Visible)
            return;
        var local = interpolate(instance, interpolationFraction);
        var transform = local.Transform.Then(parentTransform);
        var color = local.Color.Then(parentColor);
        var blend = instance.BlendMode == 8 ? LumenRenderBlend.Add : parentBlend;
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
                    color.Add,
                    blend));
            }
        }

        foreach (var child in instance.Children.Values)
            appendInstance(child, transform, color, blend, interpolationFraction, quads);
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

    private static void collectInstancesPostOrder(DisplayInstance instance, List<DisplayInstance> destination)
    {
        if (instance.Removed)
            return;
        foreach (var child in instance.Children.Values)
            collectInstancesPostOrder(child, destination);
        destination.Add(instance);
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

    private sealed class DisplayInstance(uint characterId, int hierarchyDepth, DisplayInstance? parent)
    {
        public uint CharacterId { get; } = characterId;

        public int HierarchyDepth { get; } = hierarchyDepth;

        public DisplayInstance? Parent { get; } = parent;

        public string Name { get; set; } = "";

        public uint PlacementId { get; set; } = uint.MaxValue;

        public int FirstFrame { get; set; }

        public int Frame { get; set; } = -1;

        public bool Removed { get; set; }

        public bool Playing { get; set; } = true;

        public bool Visible { get; set; } = true;

        public ushort BlendMode { get; set; }

        public LumenMatrix Transform { get; set; } = LumenMatrix.Identity;

        public ColorState Color { get; set; } = ColorState.Identity;

        public LumenMatrix? PreviousTransform { get; set; }

        public LumenRenderColor? PreviousMultiply { get; set; }

        public SortedDictionary<uint, DisplayInstance> Children { get; } = [];

        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);

        public Avm1Object? ScriptPrototype { get; set; }

        public Avm1FunctionValue? ConstructedClass { get; set; }
    }

    private readonly record struct SpriteTimeline(
        uint CharacterId,
        string? ExportName,
        ImmutableArray<ImmutableArray<LmbTimelineCommand>> Frames,
        ImmutableDictionary<int, ImmutableArray<LmbTimelineCommand>> KeyFrames,
        ImmutableDictionary<string, int> Labels);

    private readonly record struct InterpolatedState(LumenMatrix Transform, ColorState Color);

    private readonly record struct PendingFrameAction(
        DisplayInstance Instance,
        uint ActionIndex,
        long Sequence);

    private readonly record struct CallbackRegistration(
        DisplayInstance Instance,
        object? ThisValue,
        Avm1FunctionValue Function);

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
