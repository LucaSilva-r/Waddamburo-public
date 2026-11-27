using System.Globalization;
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
public sealed partial class LumenPlayer : IAvm1Host
{
    private const float TranslationCutThreshold = 200f;
    private const float ColorCutThreshold = 0.3f;
    /// <summary>A clip placed by a timeline at depth d sits at this plus d (script-made clips use depths of their own).</summary>
    public const long TimelineDepthBase = -16384;

    private readonly LmbMovieDefinition _movie;
    private readonly LumenRuntimeLimits _limits;
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, SpriteTimeline> _sprites;
    private readonly Dictionary<uint, LmbTextDefinition> _texts;
    private readonly List<LumenRuntimeDiagnostic> _diagnostics = [];
    // Reported diagnostics by (code, character, frame, detail): a tuple, so the per-frame check builds no string.
    private readonly HashSet<(string Code, uint Character, int Frame, string Detail)> _diagnosticKeys = [];
    private readonly List<PendingFrameAction> _pendingActions = [];
    private readonly List<(DisplayInstance Instance, Avm1FunctionValue Class)> _pendingConstructions = [];
    private int _frameDepth;
    private int _actionDepth;
    private bool _constructing;
    // Clips a goto creates on the frames it passes through (their scripts are not queued there).
    private List<DisplayInstance>? _gotoBorn;
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Avm1FunctionValue> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallbackRegistration> _callbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeFillBinding> _nativeFills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string[] Path, (LumenNativeSurfaceKey Surface, LumenNativeSurfacePlacement Box, bool Replace) Overlay)> _overlays =
        new(StringComparer.Ordinal);
    private readonly Avm1Object _externalInterface = new();
    private readonly DisplayInstance _root;
    private LumenInputSnapshot _inputSnapshot = LumenInputSnapshot.Empty;
    private long _nextActionSequence;
    private int _callDepth;
    private Avm1ExecutionContext? _activeContext;

    private readonly bool _scriptsEnabled;

    public LumenPlayer(
        LmbMovieDefinition movie,
        float stageWidth,
        float stageHeight,
        uint? rootCharacterId = null,
        LumenRuntimeLimits? limits = null,
        ILumenHostBinding? hostBinding = null,
        Func<string, LumenLoadedMovie?>? movieLoader = null,
        bool scriptsEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(movie);
        MovieLoader = movieLoader;
        _scriptsEnabled = scriptsEnabled;
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));

        _movie = movie;
        _limits = limits ?? LumenRuntimeLimits.Default;
        StageWidth = stageWidth;
        StageHeight = stageHeight;
        _shapes = movie.Shapes.ToDictionary(shape => shape.CharacterId);
        _texts = movie.Texts.ToDictionary(text => text.CharacterId);
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
        _globals["MovieClipLoader"] = new MovieClipLoaderConstructor();
        _globals["Date"] = new DateConstructor();
        installKeyObject();
        installMathObject();
        installExternalInterface();
        _globals["parseInt"] = new Avm1NativeFunction("parseInt", call =>
        {
            var text = call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Text
                ? call.Arguments[0].AsString().Trim() : call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Number
                    ? ((long)call.Arguments[0].AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            var radix = call.Arguments.Length > 1 && call.Arguments[1].Kind == LumenHostValueKind.Number ? (int)call.Arguments[1].AsNumber() : 10;
            // ponytail: radix 10 and 16 only (leading digits, optional sign), NaN otherwise.
            var sign = text.StartsWith('-') ? -1 : 1;
            text = text.TrimStart('-', '+');
            if (radix == 16 || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                radix = 16;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            }
            var digits = new string(text.TakeWhile(c => radix == 16 ? Uri.IsHexDigit(c) : char.IsAsciiDigit(c)).ToArray());
            return LumenHostValue.FromNumber(digits.Length == 0 || radix is not (10 or 16) ? double.NaN
                : sign * (double)Convert.ToInt64(digits, radix));
        });
        _globals["Wd"] = createProvider();
        // A movie whose scripts name the provider is one of ours from the start, before its first frame has run (the
        // engine may drive a fresh note movie in the same tick). None of the game's 701 movies has the string.
        UsesProvider = movie.Strings.Any(static text => text.Value == "Wd");
        hostBinding?.Install(new LumenHostContext(_globals, _externalInterface, findNumericVariableName));
        bootstrapPackageClasses();
        enterFrame(_root, 0, queueActions: true);
        drainActions();
    }

    public float StageWidth { get; }

    /// <summary>
    /// Resolves MovieClipLoader.loadClip URLs (as authored, e.g. "../indicator/time_counter.lm") to a
    /// player whose textures the host placed at <see cref="LumenLoadedMovie.TextureOffset"/> in this
    /// player's texture namespace. Null (the default): loadClip is reported and ignored. Only the
    /// movies' standalone developer branch (no Lumen object) loads movies this way.
    /// </summary>
    public Func<string, LumenLoadedMovie?>? MovieLoader { get; set; }

    /// <summary>Authored host notifications. No browser, process, or network action is performed.</summary>
    public event Action<string, string>? HostCommand;

    public float StageHeight { get; }

    public int CurrentFrame => _root.Frame;

    public int FrameCount => _sprites[_root.CharacterId].Frames.Length;

    public bool IsPlaying => _root.Playing;

    public ImmutableDictionary<string, int> Labels => _sprites[_root.CharacterId].Labels;

    public ImmutableArray<LumenRuntimeDiagnostic> Diagnostics => [.. _diagnostics];

    public ImmutableArray<string> CallbackNames => [.. _callbacks.Keys.Order(StringComparer.Ordinal)];

    public ImmutableArray<LumenCallbackDescriptor> Callbacks =>
        [.. _callbacks
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new LumenCallbackDescriptor(
                pair.Key,
                [.. pair.Value.Function.Definition.Parameters.Select(parameter =>
                    parameter.NameStringIndex < _movie.Strings.Length
                        ? _movie.Strings[parameter.NameStringIndex].Value
                        : $"arg{parameter.Register ?? 0}")]))];

    /// <summary>
    /// Maps one authored fill-zero clip name to a host-owned texture surface. "ancestor/name" maps it only
    /// under the clip named ancestor (e.g. one board of several that each carry the same slot names).
    /// </summary>
    public void SetNativeFill(string instanceName, LumenNativeSurfaceKey surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        used(true, "fill", instanceName);
        if (_nativeFills.TryAdd(instanceName, new NativeFillBinding(surface, null)))
            _scopedFills += instanceName.Contains('/') ? 1 : 0;
        else
            _nativeFills[instanceName] = new NativeFillBinding(surface, null);
    }

    public void SetNativeFill(
        string instanceName,
        LumenNativeSurfaceKey surface,
        LumenNativeSurfacePlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Y)
            || !float.IsFinite(placement.Width) || placement.Width <= 0
            || !float.IsFinite(placement.Height) || placement.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }
        used(true, "fill", instanceName);
        _nativeFills[instanceName] = new NativeFillBinding(surface, placement);
    }

    /// <summary>
    /// Diagnostics (F4 in the game window): quads carry <see cref="LumenRenderQuad.Source"/>, what drew
    /// them. Off by default: building the strings every frame is not free.
    /// </summary>
    public static bool Inspect { get; set; }

    /// <summary>This player stands in for a movie that exists nowhere: empty, and it accepts any callback.</summary>
    public bool StandIn { get; init; }

    /// <summary>Clips anchored to a stage edge, by their holder's character id and their depth (a package's "anchor").</summary>
    public IReadOnlyDictionary<(uint Sprite, long Depth), Rendering.LumenAnchor>? ClipAnchors { get; init; }

    /// <summary>9-sliced shapes by character id: insets from each side, in the shape's units (a package's "slice").</summary>
    public IReadOnlyDictionary<uint, (float Left, float Top, float Right, float Bottom)>? ShapeSlices { get; init; }

    /// <summary>Where the scene holds this movie (its layer's anchor): its clips' anchors are kept to the stage's edges.</summary>
    public Rendering.LumenAnchor SceneAnchor { get; set; }

    /// <summary>How wide the scene draws this movie (its layer's horizontal scale): its clips' anchors are in stage units.</summary>
    public float SceneScale { get; set; } = 1;

    /// <summary>Where the scene draws this movie's origin across (its layer's place, before its anchor).</summary>
    public float SceneX { get; set; }

    /// <summary>The movie this player runs (for <see cref="Inspect"/>).</summary>
    public string? Name
    {
        get;
        set
        {
            field = value;
            flushEdges();
        }
    }

    private long _tick;
    // Crossings made before the host named the movie (its first frame runs in the constructor).
    private List<(long Tick, string Kind, string Text)>? _unnamedEdges;
    private Dictionary<string, string>? _edgeResults;

    private void edge(string kind, string text)
    {
        if (!LumenEdgeLog.Enabled)
            return;
        if (Name is null && _tick == 0)
            (_unnamedEdges ??= []).Add((_tick, kind, text));
        else
            LumenEdgeLog.Write(_tick, Name ?? "movie", kind, text);
    }

    private void flushEdges()
    {
        if (_unnamedEdges is not { } pending)
            return;
        _unnamedEdges = null;
        foreach (var (tick, kind, text) in pending)
            LumenEdgeLog.Write(tick, Name ?? "movie", kind, text);
    }

    // Something the host addresses inside the movie by name: written when its result changes only.
    // (The name is put together here, so nothing is built while recording is off.)
    private T used<T>(T result, string operation, string name, string? member = null)
    {
        if (!LumenEdgeLog.Enabled)
            return result;
        var what = member is null ? $"{operation} {name}" : $"{operation} {name}{member}";
        var text = result switch
        {
            null => "null",
            LumenHostValue value => describe(value),
            DisplayInstance => "clip",
            Avm1Object or Dictionary<string, object?> => "object",
            _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "",
        };
        if (!(_edgeResults ??= new(StringComparer.Ordinal)).TryGetValue(what, out var last) || last != text)
        {
            _edgeResults[what] = text;
            edge("use", $"{what} -> {text}");
        }
        return result;
    }

    private DisplayInstance? hostFind(string path, string what)
    {
        var instance = findInstance(path);
        used(instance is not null, what, path);
        return instance;
    }

    private static string describe(IEnumerable<LumenHostValue> arguments) => string.Join(", ", arguments.Select(describe));

    // "movie: texture 259 — character 694 in 696 'icon' frame 3 < 744 < root".
    private string describe(DisplayInstance instance, string what)
    {
        var chain = new List<string>();
        for (var node = instance; node is not null; node = node.Parent)
            chain.Add(node.Name.Length == 0 ? $"#{node.CharacterId}" : $"#{node.CharacterId} '{node.Name}'");
        return $"{Name ?? "movie"}: {what}, frame {instance.Frame}: {string.Join(" < ", chain)}";
    }

    // Fills set as "ancestor/slot" (see SetNativeFill) apply under that ancestor only, before the plain slot.
    private int _scopedFills;

    private NativeFillBinding? scopedFill(DisplayInstance instance, string slot)
    {
        if (_scopedFills == 0)
            return null;
        // "ancestor/slot" built on the stack and looked up as a span: this runs for every fill quad, every frame.
        var lookup = _nativeFills.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> name = stackalloc char[256];
        for (var ancestor = instance.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            var length = ancestor.Name.Length + 1 + slot.Length;
            if (ancestor.Name.Length == 0 || length > name.Length)
                continue;
            ancestor.Name.CopyTo(name);
            name[ancestor.Name.Length] = '/';
            slot.CopyTo(name[(ancestor.Name.Length + 1)..]);
            if (lookup.TryGetValue(name[..length], out var fill))
                return fill;
        }
        return null;
    }

    private readonly Dictionary<uint, LumenRenderColor> _colorOverrides = [];


    /// <summary>
    /// Sets colour-pool entry <paramref name="index"/> (as a placement's multiply or add) for placements made from
    /// now on; a patch reserves such entries for colours the host picks (e.g. custom genre tints).
    /// </summary>
    public void SetColorTransform(uint index, LumenRenderColor color) => _colorOverrides[index] = color;

    public bool RemoveNativeFill(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        var removed = _nativeFills.Remove(instanceName);
        if (removed && instanceName.Contains('/'))
            _scopedFills--;
        return removed;
    }

    public bool TryInvokeCallback(string name, IReadOnlyList<LumenHostValue> arguments) =>
        TryInvokeCallback(name, arguments, out _);

    /// <summary>Calls an exported callback and hands back what it returned (song_select's GetSession).</summary>
    public bool TryInvokeCallback(string name, IReadOnlyList<LumenHostValue> arguments, out LumenHostValue result)
    {
        result = LumenHostValue.Undefined;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        // A stand-in for a movie that does not exist takes every call and answers undefined.
        if (StandIn && !_callbacks.ContainsKey(name))
            return true;
        if (!_callbacks.TryGetValue(name, out var callback))
        {
            if (LumenEdgeLog.Enabled)
                edge("in", $"{name}({describe(arguments)}) -> (no such callback)");
            return false;
        }
        var converted = arguments.Select(fromHostValue).ToArray();
        var call = invokeFunction(
            callback.Instance,
            callback.Function,
            callback.ThisValue,
            converted,
            _activeContext);
        if (!call.Found)
            return false;
        result = toHostValue(call.Value);
        if (LumenEdgeLog.Enabled)
            edge("in", $"{name}({describe(arguments)}) -> {describe(result)}");
        drainActions();
        return true;
    }

    /// <summary>
    /// Calls a script method on the object at <paramref name="targetPath"/> (as <see cref="ReadScriptValue"/>
    /// finds it), for authored helpers the movie exports no callback for. False when either is missing.
    /// </summary>
    public bool TryCallScriptMethod(string targetPath, string method, IReadOnlyList<LumenHostValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(arguments);
        // A script object, or a clip (a class registered on it gives its methods through its prototype).
        var target = readScriptValue(targetPath);
        var function = target switch
        {
            Avm1Object script => script.GetProperty(method).Value,
            DisplayInstance clip => clip.ScriptPrototype?.GetProperty(method).Value,
            _ => null,
        };
        if (LumenEdgeLog.Enabled)
            edge("call", $"{targetPath}.{method}({describe(arguments)}){(function is Avm1FunctionValue ? "" : " -> (missing)")}");
        if (target is null || function is not Avm1FunctionValue callable)
            return false;
        var invoked = invokeFunction(_root, callable, target, arguments.Select(fromHostValue).ToArray(), _activeContext).Found;
        if (invoked)
            drainActions();
        return invoked;
    }

    /// <summary>
    /// Wraps a script method of the object at <paramref name="targetPath"/> (set on that object, so a
    /// class prototype wraps every instance's): <paramref name="host"/> gets each call's arguments and a
    /// delegate that runs the original (with the real this) and returns its result. False when the
    /// object or method is missing.
    /// </summary>
    public bool TryWrapScriptMethod(string targetPath, string method,
        Func<LumenHostCall, Func<LumenHostValue>, LumenHostValue> host)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(host);
        if (readScriptValue(targetPath) is not Avm1Object target
            || target.GetProperty(method).Value is not Avm1FunctionValue original)
            return used(false, "wrap", targetPath, "." + method);
        used(true, "wrap", targetPath, "." + method);
        target.Properties[method] = new Avm1MethodWrapper((self, arguments) => new Avm1Lookup(true, fromHostValue(host(
            new LumenHostCall([.. arguments.Select(toHostValue)]),
            () => toHostValue(invokeFunction(_root, original, self, arguments, _activeContext).Value)))));
        return true;
    }

    /// <summary>
    /// Calls a script method without arguments on the object at <paramref name="targetPath"/> and returns
    /// its result, or the result's <paramref name="member"/>, when that is a number; null otherwise.
    /// For polling authored state the movie reports to no callback (e.g. Song Select's folder cursor).
    /// </summary>
    public double? CallScriptNumber(string targetPath, string method, string? member = null, params double[] arguments)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var what = LumenEdgeLog.Enabled ? $".{method}({string.Join(", ", arguments)}){(member is null ? "" : "." + member)}" : "";
        if (readScriptValue(targetPath) is not Avm1Object target
            || target.GetProperty(method).Value is not Avm1FunctionValue function)
            return used((double?)null, "call", targetPath, what);
        var result = invokeFunction(_root, function, target, [.. arguments.Select(static argument => (object?)argument)], _activeContext).Value;
        if (member is not null)
            result = result is Avm1Object returned ? returned.GetProperty(member).Value : null;
        return used(result is double number ? number : (double?)null, "call", targetPath, what);
    }

    public void Advance()
        => Advance(LumenInputSnapshot.Empty);

    // TryFastForward already recorded the transforms this tick's interpolation starts from.
    private bool _tickSnapshotTaken;

    public void Advance(LumenInputSnapshot inputSnapshot)
    {
        ArgumentNullException.ThrowIfNull(inputSnapshot);
        _inputSnapshot = inputSnapshot;
        flushEdges();
        _tick++;
        // A clip run ahead for this tick already recorded where the tick's motion starts.
        if (!_tickSnapshotTaken)
            snapshotInstances(_root);
        _tickSnapshotTaken = false;
        advanceSubtree(_root, queueActions: true);
        drainActions();
        dispatchEnterFrameHandlers();
        drainActions();
        advanceLoadedMovies(inputSnapshot);
    }

    private readonly List<DisplayInstance> _loadedClips = [];
    private readonly List<(Avm1Object Listener, DisplayInstance Target)> _pendingLoadInits = [];

    private void advanceLoadedMovies(LumenInputSnapshot inputSnapshot)
    {
        _loadedClips.RemoveAll(static clip => clip.Removed);
        foreach (var clip in _loadedClips.ToArray())
            clip.LoadedMovie!.Player.Advance(inputSnapshot);
        // Flash calls onLoadInit once the loaded movie's first frame has run.
        var pending = _pendingLoadInits.ToArray();
        _pendingLoadInits.Clear();
        foreach (var (listener, target) in pending)
            if (listener.GetProperty("onLoadInit").Value is Avm1FunctionValue onLoadInit)
                invokeFunction(_root, onLoadInit, listener, [target], _activeContext);
        if (pending.Length != 0)
            drainActions();
    }

    /// <summary>A method called on a clip holding a loaded movie: its root function or callback.</summary>
    private static Avm1Lookup callLoadedMovie(LumenPlayer child, string name, IReadOnlyList<object?> arguments)
    {
        var converted = arguments.Select(toHostValue).ToArray();
        if (child._root.Variables.GetValueOrDefault(name) is Avm1FunctionValue function)
        {
            if (LumenEdgeLog.Enabled)
                child.edge("in", $"{name}({describe(converted)}) (root function, from the loading movie)");
            var result = child.invokeFunction(child._root, function, child._root,
                [.. converted.Select(fromHostValue)], child._activeContext);
            child.drainActions();
            return new Avm1Lookup(true, fromHostValue(toHostValue(result.Value)));
        }
        return new Avm1Lookup(child.TryInvokeCallback(name, converted), Avm1Undefined.Instance);
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
        if (LumenEdgeLog.Enabled)
            edge("root", $"GotoFrame({frame}, {play})");
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
        if (LumenEdgeLog.Enabled)
            edge("root", $"GotoLabel({label}, {play})");
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var timeline = _sprites[_root.CharacterId];
        if (!timeline.Labels.TryGetValue(label, out var frame))
        {
            // A stand-in (a movie with no free version yet) has no labels: the engine's seeks are no-ops on it.
            if (StandIn)
                return;
            throw new KeyNotFoundException($"Label '{label}' is not defined on root character {_root.CharacterId}.");
        }
        GotoFrame(frame, play);
    }

    public void Play() => _root.Playing = true;

    public void Stop() => _root.Playing = false;

    /// <summary>Seeks an explicitly named child path without exposing runtime objects.</summary>
    public bool TryGotoLabel(string instancePath, string label, bool play = true)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        // A stand-in takes every seek, like every call (see TryInvokeCallback).
        if (StandIn)
            return true;
        var instance = findInstance(instancePath);
        if (instance is null || !_sprites.TryGetValue(instance.CharacterId, out var timeline)
            || !timeline.Labels.TryGetValue(label, out var frame))
        {
            if (LumenEdgeLog.Enabled)
                edge("goto", $"{instancePath} '{label}' -> (missing)");
            return false;
        }
        if (LumenEdgeLog.Enabled)
            edge("goto", $"{instancePath} '{label}'{(play ? "" : " stop")}");
        jumpInstance(instance, frame, play);
        drainActions();
        resetInterpolation(instance);
        return true;
    }

    /// <summary>Seeks a named clip to a zero-based frame (a script's gotoAndStop(n) is frame n - 1).</summary>
    public bool TryGotoFrame(string instancePath, int frame, bool play = true)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        var instance = hostFind(instancePath, "frame");
        if (instance is null || !_sprites.TryGetValue(instance.CharacterId, out var timeline)
            || frame < 0 || frame >= timeline.Frames.Length)
            return false;
        jumpInstance(instance, frame, play);
        drainActions();
        resetInterpolation(instance);
        return true;
    }

    /// <summary>The frame a named clip is on and whether it is playing; null when there is no such clip.</summary>
    public (int Frame, bool Playing)? InstanceFrame(string instancePath)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        return hostFind(instancePath, "frame") is { } instance ? (instance.Frame, instance.Playing) : null;
    }

    /// <summary>
    /// Whether a named clip and its ancestors are present and visible to script and host visibility controls.
    /// Alpha is ignored: a transparent panel can already own input while its fade-in plays.
    /// </summary>
    public bool IsInstanceVisible(string instancePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        var instance = findInstance(instancePath);
        if (instance is null) return false;
        for (var current = instance; current is not null; current = current.Parent)
            if (current.Removed || !current.Visible || current.HiddenByHost) return false;
        return true;
    }

    /// <summary>The frame a label names on a named clip's timeline.</summary>
    public bool TryGetLabelFrame(string instancePath, string label, out int frame)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        frame = 0;
        return hostFind(instancePath, "label") is { } instance && _sprites.TryGetValue(instance.CharacterId, out var timeline)
            && timeline.Labels.TryGetValue(label, out frame);
    }

    /// <summary>
    /// Runs a named clip ahead of the rest of the movie, until it is on <paramref name="frame"/>: each
    /// step is a whole frame for the clip and everything in it (timelines, frame scripts, enter-frame
    /// handlers), so its animations stay consistent with each other; a script that stops the clip or moves
    /// its playhead ends it. Call it before <see cref="Advance(LumenInputSnapshot)"/>: the tick then
    /// interpolates from where the clip was shown last, through the frames run here.
    /// False when the clip is missing, stopped, or already there.
    /// </summary>
    public bool TryFastForward(string instancePath, int frame)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        var instance = hostFind(instancePath, "frame");
        if (instance is null || !instance.Playing || !_sprites.TryGetValue(instance.CharacterId, out var timeline)
            || frame <= instance.Frame || frame >= timeline.Frames.Length)
            return false;
        if (!_tickSnapshotTaken)
            snapshotInstances(_root);
        _tickSnapshotTaken = true;
        while (instance.Playing && !instance.Removed && instance.Frame < frame)
        {
            var next = instance.Frame + 1;
            advanceSubtree(instance, queueActions: true);
            drainActions();
            dispatchEnterFrameHandlers(instance);
            drainActions();
            if (instance.Frame != next)
                break;
        }
        return true;
    }

    /// <summary>Sets the alpha of a named clip that a native scene callback controls.</summary>
    public bool TrySetInstanceAlpha(string instancePath, float alpha)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        if (!float.IsFinite(alpha) || alpha is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(alpha));
        var instance = hostFind(instancePath, "alpha");
        if (instance is null)
            return false;
        instance.Color = instance.Color with
        {
            Multiply = instance.Color.Multiply with { Alpha = alpha },
        };
        resetInterpolation(instance);
        return true;
    }

    // Clips the host has offset (SetInstanceOffset), and by how much: added on top of where their timeline places them.
    // ponytail: entries of clips the timeline removed stay until the host sets their offset back to (0, 0).
    private readonly Dictionary<DisplayInstance, (float X, float Y)> _hostOffsets = [];

    /// <summary>
    /// Moves a named clip by (x, y) in its parent's space on top of its own placement: its timeline keeps animating it
    /// (a clip that scales in still does), moved. (0, 0) puts it back. Absolute, not cumulative.
    /// </summary>
    public bool SetInstanceOffset(string instancePath, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        if (hostFind(instancePath, "offset") is not { } instance)
            return false;
        var old = _hostOffsets.GetValueOrDefault(instance);
        if (old == (x, y))
            return true;
        instance.Transform = instance.Transform with { X = instance.Transform.X + x - old.X, Y = instance.Transform.Y + y - old.Y };
        if (x == 0 && y == 0) _hostOffsets.Remove(instance);
        else _hostOffsets[instance] = (x, y);
        return true;
    }

    /// <summary>
    /// Reads a script variable or named clip by dotted path from the root (e.g. "main.isAllEnd"), as the game
    /// polls movie state. Null when any step is missing.
    /// </summary>
    private readonly Dictionary<string, string[]> _scriptPaths = new(StringComparer.Ordinal);

    private static DisplayInstance? childNamed(DisplayInstance instance, string name)
    {
        var children = instance.Children.Values;
        for (var index = 0; index < children.Count; index++)
            if (children[index].Name == name)
                return children[index];
        return null;
    }

    public object? ReadScriptValue(string path) => used(readScriptValue(path), "read", path);

    private object? readScriptValue(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Hosts read the same paths every tick: split once.
        if (!_scriptPaths.TryGetValue(path, out var parts))
            _scriptPaths[path] = parts = path.Split('.');
        // "_global.x" starts at the script globals, anything else at the root timeline.
        var global = parts[0] == "_global";
        object? value = global ? _globals.GetValueOrDefault("_global") : _root;
        for (var index = global ? 1 : 0; index < parts.Length; index++)
        {
            var part = parts[index];
            value = value switch
            {
                DisplayInstance instance => instance.Variables.GetValueOrDefault(part) ?? childNamed(instance, part),
                Avm1ArrayObject array => array.GetIndexedProperty(part).Value,
                Avm1Object script => script.Properties.GetValueOrDefault(part),
                Dictionary<string, object?> globals => globals.GetValueOrDefault(part),
                _ => null,
            };
            if (value is null) return null;
        }
        return value;
    }

    /// <summary>
    /// Writes a primitive (number, string, bool) to a script variable by dotted path, as
    /// <see cref="ReadScriptValue"/> reads it (array elements by index). False when the parent is missing.
    /// </summary>
    public bool TryWriteScriptValue(string path, object value)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (value is not (double or string or bool))
            throw new ArgumentException("Only numbers, strings and booleans cross into script.", nameof(value));
        var split = path.LastIndexOf('.');
        if (split <= 0)
            return false;
        var name = path[(split + 1)..];
        used(value, "write", path);
        switch (readScriptValue(path[..split]))
        {
            case DisplayInstance instance:
                instance.Variables[name] = value;
                return true;
            case Avm1ArrayObject array:
                return array.TrySetIndexedProperty(name, value, _limits.MaxStackValues);
            case Avm1Object script:
                script.Properties[name] = value;
                return true;
            case Dictionary<string, object?> globals:
                globals[name] = value;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether an explicitly named child path exists and its timeline is playing.</summary>
    public bool IsInstancePlaying(string instancePath)
    {
        ArgumentNullException.ThrowIfNull(instancePath);
        return hostFind(instancePath, "playing")?.Playing == true;
    }

    /// <summary>Diagnostic dump of the display list (name, character, frame/label, playing, visible).</summary>
    public IEnumerable<string> DescribeDisplayList()
    {
        var lines = new List<string>();
        void visit(DisplayInstance instance, int level)
        {
            var label = _sprites.TryGetValue(instance.CharacterId, out var timeline)
                ? timeline.Labels.Where(pair => pair.Value <= instance.Frame).OrderByDescending(pair => pair.Value)
                    .Select(pair => pair.Key).FirstOrDefault() : null;
            lines.Add($"{new string(' ', 2 * level)}{(instance.Name.Length > 0 ? instance.Name : "-")} ch={instance.CharacterId} "
                + $"frame={instance.Frame}{(label is null ? "" : $"({label})")}{(instance.Playing ? " playing" : "")}"
                + $"{(instance.Visible ? "" : " HIDDEN")} a={instance.Color.Multiply.Alpha:0.##} at=({instance.Transform.X:0},{instance.Transform.Y:0})");
            foreach (var child in childrenOf(instance))
                visit(child, level + 1);
        }
        visit(_root, 0);
        return lines;
    }

    /// <summary>Keeps the whole movie from being drawn (it still runs), e.g. a scene's layer the game has finished with.</summary>
    public bool Hidden
    {
        get => _root.HiddenByHost;
        set => _root.HiddenByHost = value;
    }

    /// <summary>Keeps a clip from being drawn whatever its script does with _visible, until asked otherwise.</summary>
    public bool SetInstanceHidden(string instancePath, bool hidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        if (hostFind(instancePath, "hidden") is not { } instance)
            return false;
        instance.HiddenByHost = hidden;
        return true;
    }

    /// <summary>
    /// Draws a host surface over every clip named <paramref name="instancePath"/> ("a/b/name" scopes it to
    /// clips under those ancestors),
    /// in that clip's own coordinates: it follows the clip's transform, colour and visibility, like a text
    /// field would. Used for the name boards' names (the board's own glyphs are kana only). Null removes it.
    /// <paramref name="replace"/>: drawn instead of the clip's own content (its children), not over it.
    /// </summary>
    public void SetInstanceOverlay(string instancePath, LumenNativeSurfaceKey? surface, LumenNativeSurfacePlacement box = default,
        bool replace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        if (surface is { } key)
            _overlays[instancePath] = ([.. instancePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()], (key, box, replace));
        else
            _overlays.Remove(instancePath);
    }

    // The overlay whose path ends with this clip and its nearest ancestors ("board1p/name_board/names").
    // ponytail: markers carry no rotation, so only the scale's area is kept.
    private static LumenMatrix uniformScale(LumenMatrix transform)
    {
        var scale = MathF.Sqrt(MathF.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        return transform with { M11 = scale, M12 = 0, M21 = 0, M22 = scale };
    }

    private (LumenNativeSurfaceKey Surface, LumenNativeSurfacePlacement Box, bool Replace)? findOverlay(DisplayInstance instance)
    {
        foreach (var (path, overlay) in _overlays.Values) // paths are split once, clip first: this runs for every named clip drawn
        {
            var node = instance;
            var matched = true;
            foreach (var name in path)
            {
                if (node is null || node.Name != name) { matched = false; break; }
                node = node.Parent;
            }
            if (matched) return overlay;
        }
        return null;
    }

    /// <summary>A named clip's transform to movie coordinates (its own and its parents').</summary>
    public bool TryGetInstanceTransform(string instancePath, out LumenMatrix transform)
    {
        transform = LumenMatrix.Identity;
        if (hostFind(instancePath, "transform") is not { } instance)
            return false;
        for (var node = instance; node is not null; node = node.Parent)
            transform = transform.Then(node.Transform);
        return true;
    }

    /// <summary>Returns the rendered bounds of a named clip in movie coordinates.</summary>
    public bool TryGetInstanceBounds(string instancePath, out LumenNativeSurfacePlacement bounds)
    {
        var instance = hostFind(instancePath, "bounds");
        bounds = default;
        if (instance is null)
            return false;
        var transform = LumenMatrix.Identity;
        for (var parent = instance.Parent; parent is not null; parent = parent.Parent)
            transform = transform.Then(parent.Transform);
        var quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
        appendInstance(instance, transform, ColorState.Identity, LumenRenderBlend.Normal, 1, quads);
        var vertices = quads.Where(q => q.MaskOperation == LumenRenderMaskOperation.Draw)
            .SelectMany(q => new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight }).ToArray();
        if (vertices.Length == 0) return false;
        var x = vertices.Min(v => v.X);
        var y = vertices.Min(v => v.Y);
        bounds = new(x, y, vertices.Max(v => v.X) - x, vertices.Max(v => v.Y) - y);
        return true;
    }

    // A clip by its names from the root ("a/b/name"), else the first live clip anywhere whose own and
    // nearest ancestors' names end the path (as SetInstanceOverlay matches clips).
    private DisplayInstance? findInstance(string path)
    {
        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var instance = _root;
        foreach (var name in names)
        {
            // "#<id>" names an unnamed child by its character id.
            instance = instance.Children.Values.FirstOrDefault(child => !child.Removed
                && (child.Name == name || name[0] == '#' && child.Name.Length == 0 && name.AsSpan(1).SequenceEqual(child.CharacterId.ToString(CultureInfo.InvariantCulture))));
            if (instance is null)
                return names.Length == 0 ? null : findBySuffix(_root, names);
        }
        return instance;
    }

    private static DisplayInstance? findBySuffix(DisplayInstance instance, string[] names)
    {
        foreach (var child in childrenOf(instance))
        {
            if (child.Removed)
                continue;
            if (child.Name == names[^1])
            {
                var node = child.Parent;
                var matched = true;
                for (var index = names.Length - 2; index >= 0 && matched; index--, node = node?.Parent)
                    matched = node is not null && node.Name == names[index];
                if (matched)
                    return child;
            }
            if (findBySuffix(child, names) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// A script member of a clip found by name (as <see cref="TryGetInstanceTransform"/> finds it, the path's
    /// end matched anywhere), e.g. a selector's current index. Undefined when either is missing.
    /// </summary>
    /// <summary>Writes a number to a script member of a clip found as <see cref="ReadClipMember"/> finds it. False when the clip is missing.</summary>
    public bool TryWriteClipMember(string clipPath, string member, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);
        used(value, "write", clipPath, ":" + member);
        if (findInstance(clipPath) is not { } instance)
            return false;
        instance.Variables[member] = value;
        return true;
    }

    public LumenHostValue ReadClipMember(string clipPath, string member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);
        return used(findInstance(clipPath)?.Variables.GetValueOrDefault(member) is { } value ? toHostValue(value) : LumenHostValue.Undefined,
            "read", clipPath, LumenEdgeLog.Enabled ? ":" + member : null);
    }

    private void bootstrapPackageClasses()
    {
        foreach (var sprite in _movie.Sprites)
        {
            var timeline = _sprites[sprite.CharacterId];
            if (timeline.ExportName?.StartsWith("__Packages.", StringComparison.Ordinal) != true)
                continue;
            if (timeline.Frames.IsEmpty)
                continue;
            var package = new DisplayInstance(timeline.CharacterId, hierarchyDepth: 0, parent: null)
            {
                Name = timeline.ExportName!,
                Frame = 0,
                Playing = false,
            };
            foreach (var action in timeline.Frames[0].OfKind(LmbCommandKind.DoAction))
            {
                if (action.ActionIndex < (uint)_movie.Actions.Length)
                {
                    _ = executeAction(
                        package,
                        _movie.Actions[checked((int)action.ActionIndex)].Code,
                        captureTimelineTarget: false);
                    foreach (var (name, value) in package.Variables)
                        _globals[name] = value;
                    package.Variables.Clear();
                }
            }
        }
    }

    private string? findNumericVariableName(string prefix, double value)
    {
        if (_activeContext is null)
            return null;
        foreach (var name in _movie.Strings
                     .Select(static value => value.Value)
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var candidate = _activeContext.GetVariable(name);
            if (candidate.Found && candidate.Value is double number && number == value)
                return name;
        }
        return null;
    }

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

    /// <summary>
    /// What is on screen now becomes the interpolation's starting point, as <see cref="Advance(LumenInputSnapshot)"/>
    /// would make it: the next snapshot draws the movie exactly as it is. For clips a script sets every frame
    /// (a roll's width, following its exact place), which must not blend from the last tick's state.
    /// </summary>
    public void HoldStill() => snapshotInstances(_root);

    // Lists reused by advanceSubtree (it runs every tick, and again inside scripts that fast-forward a clip: a pool).
    private readonly Stack<List<DisplayInstance>> _instanceLists = new();

    private void advanceSubtree(DisplayInstance root, bool queueActions)
    {
        var existing = _instanceLists.Count > 0 ? _instanceLists.Pop() : [];
        try
        {
            collectPlayingInstances(root, existing);
            for (var index = 0; index < existing.Count; index++)
            {
                if (!existing[index].Removed)
                    advanceInstance(existing[index], queueActions);
            }
        }
        finally
        {
            existing.Clear();
            _instanceLists.Push(existing);
        }
    }

    public LumenRenderSnapshot CreateRenderSnapshot(float interpolationFraction = 1f)
    {
        if (!float.IsFinite(interpolationFraction) || interpolationFraction < 0 || interpolationFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(interpolationFraction));
        // The builder keeps its capacity between frames: one exact-size array per snapshot, no regrowth.
        _quads.Clear();
        resetMasks();
        appendInstance(
            _root,
            LumenMatrix.Identity,
            ColorState.Identity,
            LumenRenderBlend.Normal,
            interpolationFraction,
            _quads);
        return new LumenRenderSnapshot(StageWidth, StageHeight, _quads.ToImmutable());
    }

    /// <summary>This frame's quads appended to <paramref name="into"/> (a scene's builder): no array of their own.</summary>
    public void AppendRenderQuads(float interpolationFraction, ImmutableArray<LumenRenderQuad>.Builder into)
    {
        if (!float.IsFinite(interpolationFraction) || interpolationFraction < 0 || interpolationFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(interpolationFraction));
        ArgumentNullException.ThrowIfNull(into);
        resetMasks();
        appendInstance(_root, LumenMatrix.Identity, ColorState.Identity, LumenRenderBlend.Normal, interpolationFraction, into);
    }

    private readonly ImmutableArray<LumenRenderQuad>.Builder _quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();

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
                case { Kind: LmbCommandKind.ShowFrame } show when show.Frame < (uint)frameCount:
                    ordinaryFrame = (int)show.Frame;
                    keyFrame = null;
                    break;
                case { Kind: LmbCommandKind.FrameKey } key when key.Frame < (uint)frameCount:
                    ordinaryFrame = null;
                    keyFrame = (int)key.Frame;
                    if (!keyBuilders.ContainsKey(keyFrame.Value))
                        keyBuilders.Add(keyFrame.Value, ImmutableArray.CreateBuilder<LmbTimelineCommand>());
                    break;
                case { Kind: LmbCommandKind.FrameLabel } label
                    when label.StringIndex < (uint)strings.Length
                        && label.Frame < (uint)frameCount:
                    // A name used twice (dance_b_20: "dance" at 80 and 399, its loop jumping from 399) means the first, as in Flash.
                    labels.TryAdd(strings[checked((int)label.StringIndex)].Value, checked((int)label.Frame));
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
            foreach (var child in childrenOf(instance))
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
        // Flash: a one-frame timeline has nowhere to go, so playing it neither loops nor re-runs its
        // frame script (cut_in's frame-0 setup would reset its state every tick).
        if (timeline.Frames.Length == 1)
            return;
        var nextFrame = instance.Frame + 1;
        if (nextFrame >= timeline.Frames.Length)
        {
            rewindTimeline(instance, 0, queueActions);
            return;
        }
        enterFrame(instance, nextFrame, queueActions);
    }

    private void rewindTimeline(DisplayInstance instance, int targetFrame, bool queueActions)
    {
        // The timeline's children come out from the end (RemoveAt shifts what follows; nothing does there): a song
        // select scroll rewinds clips with many children every tick.
        var children = instance.Children;
        var reusable = new Dictionary<long, DisplayInstance>();
        for (var index = children.Count - 1; index >= 0; index--)
        {
            var child = children.GetValueAtIndex(index);
            if (!child.FromTimeline)
                continue;
            reusable[children.GetKeyAtIndex(index)] = child;
            children.RemoveAt(index);
        }

        instance.ReusableTimelineChildren = reusable;
        instance.Frame = -1;
        try
        {
            for (var frame = 0; frame <= targetFrame; frame++)
                enterFrame(instance, frame, queueActions && frame == targetFrame);
        }
        finally
        {
            foreach (var child in reusable.Values)
                child.Removed = true;
            instance.ReusableTimelineChildren = null;
        }
        foreach (var child in childrenOf(instance))
        {
            child.PreviousTransform = null;
            child.PreviousMultiply = null;
        }
    }

    private void enterFrame(DisplayInstance instance, int frame, bool queueActions)
    {
        var timeline = _sprites[instance.CharacterId];
        if ((uint)frame >= (uint)timeline.Frames.Length)
            return;
        instance.Frame = frame;
        _frameDepth++;
        try
        {
            applyCommands(instance, timeline.Frames[frame], queueActions);
        }
        finally
        {
            _frameDepth--;
            drainConstructions();
        }
    }

    // Build the display tree first, then initialize parent state before child constructors read it.
    // Prototypes are available immediately, so a parent's constructor can still find child methods.
    private void queueConstruction(DisplayInstance instance, Avm1FunctionValue function)
    {
        instance.ScriptPrototype = function.GetProperty("prototype").Value as Avm1Object;
        _pendingConstructions.Add((instance, function));
    }

    private void drainConstructions()
    {
        if (_frameDepth != 0 || _actionDepth != 0 || _constructing) return;
        _constructing = true;
        try
        {
            while (_pendingConstructions.Count > 0)
            {
                var pending = _pendingConstructions.OrderBy(static entry => entry.Instance.HierarchyDepth).ToArray();
                _pendingConstructions.Clear();
                foreach (var (instance, function) in pending)
                    if (!instance.Removed && !ReferenceEquals(instance.ConstructedClass, function))
                        constructInstance(instance, function);
            }
        }
        finally { _constructing = false; }
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
                case { Kind: LmbCommandKind.PlaceObject } place:
                    applyPlacement(instance, place, queueActions);
                    break;
                case { Kind: LmbCommandKind.RemoveObject } remove:
                    // From PackedDepth, the word the file carries: a command made `with { PackedDepth = ... }` (the movie editor's
                    // clears) keeps its template's Depth.
                    var depth = TimelineDepthBase + (int)(remove.PackedDepth >> 16);
                    if (instance.Children.TryGetValue(depth, out var removed))
                    {
                        instance.Children.Remove(depth);
                        removed.Removed = true;
                    }
                    break;
                case { Kind: LmbCommandKind.DoAction } action when queueActions:
                    enqueueAction(instance, action);
                    break;
            }
        }
    }

    private void enqueueCurrentFrameActions(DisplayInstance instance)
    {
        if (instance.Removed || instance.Frame < 0)
            return;
        var timeline = _sprites[instance.CharacterId];
        foreach (var action in timeline.Frames[instance.Frame].OfKind(LmbCommandKind.DoAction))
        {
            enqueueAction(instance, action);
        }
        foreach (var child in childrenOf(instance))
        {
            if (_sprites.ContainsKey(child.CharacterId))
                enqueueCurrentFrameActions(child);
        }
    }

    private void drainActions()
    {
        var processed = 0;
        do
        {
            _actionDepth++;
            try { processed = drainActionBatch(processed); }
            finally
            {
                _actionDepth--;
                drainConstructions();
            }
        }
        // Constructors can seek timelines and enqueue the scripts that export host callbacks.
        // Finish those scripts before returning to the host, sharing the same action budget.
        while (_pendingActions.Count > 0);
    }

    private int drainActionBatch(int processed)
    {
        while (_pendingActions.Count > 0 && processed < _limits.MaxPendingActions)
        {
            var remaining = _limits.MaxPendingActions - processed;
            var batch = _pendingActions
                .OrderBy(action => action.Instance.HierarchyDepth)
                .ThenBy(action => action.Sequence)
                .Take(remaining)
                .ToArray();
            var dropped = _pendingActions.Count - batch.Length;
            _pendingActions.Clear();
            foreach (var pending in batch)
            {
                processed++;
                if (pending.Instance.Removed)
                    continue;
                if (!pending.IsInitialization)
                {
                    // Class initialization records form a block. Construct the registered clips
                    // before the following ordinary frame script starts using their state.
                    _actionDepth--;
                    try { drainConstructions(); }
                    finally { _actionDepth++; }
                }
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
            if (dropped == 0)
                continue;
            reportOnce(
                "LUM_ACTION_QUEUE_LIMIT",
                _root.CharacterId,
                _root.Frame,
                $"Pending AVM action limit {_limits.MaxPendingActions} was reached; {dropped} actions were dropped.");
        }
        if (_pendingActions.Count > 0)
        {
            var dropped = _pendingActions.Count;
            _pendingActions.Clear();
            reportOnce(
                "LUM_ACTION_QUEUE_LIMIT",
                _root.CharacterId,
                _root.Frame,
                $"Pending AVM action limit {_limits.MaxPendingActions} was reached; {dropped} actions were dropped.");
        }
        return processed;
    }

    private List<DisplayInstance>? _enterFrameScratch;

    private void dispatchEnterFrameHandlers(DisplayInstance? subtree = null)
    {
        // One list reused tick after tick (a handler that dispatches again, re-entering, takes a fresh one).
        var instances = _enterFrameScratch ?? [];
        _enterFrameScratch = null;
        instances.Clear();
        try
        {
            dispatchEnterFrameHandlers(subtree, instances);
        }
        finally
        {
            instances.Clear();
            _enterFrameScratch = instances;
        }
    }

    private void dispatchEnterFrameHandlers(DisplayInstance? subtree, List<DisplayInstance> instances)
    {
        collectInstancesPostOrder(subtree ?? _root, instances);
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

    private void enqueueAction(DisplayInstance instance, LmbTimelineCommand action)
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
        _pendingActions.Add(new PendingFrameAction(instance, action.ActionIndex, _nextActionSequence++,
            (action.Unknown & 0x01000000) != 0));
    }

    private Avm1ExecutionStatus executeAction(
        DisplayInstance instance,
        Avm1CodeBlock code,
        bool captureTimelineTarget = true)
    {
        if (!_scriptsEnabled) return Avm1ExecutionStatus.Success;
        var startingFrame = instance.Frame;
        var startingPlaying = instance.Playing;
        var parentContext = _activeContext;
        var context = createExecutionContext(
            instance,
            parentContext,
            lexicalScope: null,
            instance,
            captureTimelineTarget ? instance : null);
        var previousContext = _activeContext;
        _activeContext = context;
        try
        {
            var status = Avm1Interpreter.TryExecute(
                code,
                _movie.Strings,
                instance.Playing,
                _limits,
                context,
                out var playing);
            if (status == Avm1ExecutionStatus.Success)
            {
                if (context.PendingPlayback is { } requestedPlayback)
                    instance.Playing = requestedPlayback;
                else if (instance.Frame == startingFrame && instance.Playing == startingPlaying)
                    instance.Playing = playing;
                context.Commit();
            }
            else if (status == Avm1ExecutionStatus.StackUnderflow)
                reportOnce("LUM_AVM_STACK_UNDERFLOW", instance.CharacterId, instance.Frame,
                    $"AVM stack underflow at action offset 0x{context.InstructionOffset:X}.");
            return status;
        }
        finally
        {
            _activeContext = previousContext;
        }
    }

    private Avm1ExecutionContext createExecutionContext(
        DisplayInstance instance,
        Avm1ExecutionContext? parentContext,
        Avm1LexicalScope? lexicalScope,
        object? thisValue,
        object? timelineTarget) =>
        // One object per script call: the callbacks are this player's (IAvm1Host), the call's own state rides on the
        // context (a closure and eleven delegates per call were most of a song select tick's garbage).
        new(this, instance, thisValue, lexicalScope, timelineTarget, parentContext);

    Avm1Lookup IAvm1Host.ReadVariable(Avm1ExecutionContext context, string name)
    {
        var instance = (DisplayInstance)context.Owner;
        var thisValue = context.ThisValue;
        if (name == "this")
            return new Avm1Lookup(true, thisValue);
        if (name == "_root" || name == "_level0")
            return new Avm1Lookup(true, _root);
        if (name == "_parent")
            return new Avm1Lookup(instance.Parent is not null, instance.Parent);
        if (name == "_global")
            return new Avm1Lookup(true, _globals);
        var lexical = context.GetLexicalVariable(name);
        if (lexical.Found)
            return lexical;
        var instanceMember = context.GetMember(instance, name);
        return instanceMember.Found
            ? instanceMember
            : context.GetMember(_globals, name);
    }

    void IAvm1Host.WriteVariable(Avm1ExecutionContext context, string name, object? value)
    {
        var instance = (DisplayInstance)context.Owner;
        instance.Variables[name] = value;
    }

    Avm1Lookup IAvm1Host.ReadMember(Avm1ExecutionContext context, object? target, string name)
    {
        var parentContext = context.TransactionParent;
        return parentContext?.GetMember(target, name) ?? readMember(target, name);
    }

    void IAvm1Host.WriteMember(Avm1ExecutionContext context, object? target, string name, object? value)
    {
        var parentContext = context.TransactionParent;
        if (parentContext is null)
            writeMember(target, name, value);
        else
            parentContext.SetMember(target, name, value);
    }

    Avm1Lookup IAvm1Host.CallFunction(Avm1ExecutionContext context, string name, IReadOnlyList<object?> arguments)
    {
        var instance = (DisplayInstance)context.Owner;
        var candidate = context.GetVariable(name);
        if (candidate.Found && candidate.Value is Avm1FunctionValue function)
            return invokeFunction(instance, function, instance, arguments, context);
        if (candidate.Found && candidate.Value is Avm1MethodWrapper wrapper)
            return wrapper.Invoke(instance, arguments);
        if (candidate.Found && candidate.Value is Avm1NativeFunction nativeFunction)
            return invokeHost(instance, nativeFunction, arguments);
        if (name == "ASSetPropFlags")
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        // Boolean(x) as a conversion function (attract title polls its stream this way).
        if (name == "Boolean")
            return new Avm1Lookup(true, arguments.Count > 0 && toBoolean(arguments[0]));
        reportOnce(
            "LUM_AVM_CALL_UNRESOLVED",
            instance.CharacterId,
            instance.Frame,
            $"AVM function '{name}' is not registered; undefined was returned.");
        return default;
    }

    private Avm1Lookup addCallback(Avm1ExecutionContext context, DisplayInstance instance, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count >= 3
            && arguments[0] is string callbackName
            && arguments[2] is Avm1FunctionValue callbackFunction)
        {
            var thisObject = arguments[1];
            context.OnCommit(() =>
            {
                _callbacks[callbackName] = new CallbackRegistration(instance, thisObject, callbackFunction);
                if (LumenEdgeLog.Enabled)
                    edge("export", $"{callbackName}({string.Join(", ", Callbacks.First(each => each.Name == callbackName).Parameters)})");
            });
            return new Avm1Lookup(true, true);
        }
        return new Avm1Lookup(true, false);
    }

    // Array.pop, shift, join and reverse.
    private static Avm1Lookup listMethod(Avm1ExecutionContext context, Avm1ArrayObject listArray, string name, IReadOnlyList<object?> arguments)
    {
        var lengthLookup = context.GetMember(listArray, "length");
        var length = lengthLookup.Found ? toNumber(lengthLookup.Value) : 0;
        var count = double.IsFinite(length) && length > 0 ? (int)length : 0;
        string key(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (name == "join")
        {
            var separator = arguments.Count > 0 && arguments[0] is not Avm1Undefined ? toAvmString(arguments[0]) : ",";
            return new Avm1Lookup(true, string.Join(separator, Enumerable.Range(0, count).Select(index =>
                context.GetMember(listArray, key(index)) is { Found: true, Value: not (null or Avm1Undefined) } item
                    ? toAvmString(item.Value) : "")));
        }
        // In place, and the array itself (reward_shop's reward list).
        if (name == "reverse")
        {
            var items = Enumerable.Range(0, count).Select(index => context.GetMember(listArray, key(index)).Value).ToList();
            for (var index = 0; index < count; index++)
                context.SetMember(listArray, key(index), items[count - 1 - index]);
            return new Avm1Lookup(true, listArray);
        }
        if (count == 0)
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        object? removed;
        if (name == "pop")
            removed = context.GetMember(listArray, key(count - 1)).Value;
        else
        {
            removed = context.GetMember(listArray, key(0)).Value;
            for (var index = 1; index < count; index++)
                context.SetMember(listArray, key(index - 1), context.GetMember(listArray, key(index)).Value);
        }
        context.SetMember(listArray, "length", (double)(count - 1));
        return new Avm1Lookup(true, removed ?? Avm1Undefined.Instance);
    }

    // Flash 8's String methods (AS2 semantics: a negative start counts from the end where Flash does, NaN is 0).
    private static object stringMethod(string text, string name, IReadOnlyList<object?> arguments)
    {
        bool given(int index) => arguments.Count > index && arguments[index] is not Avm1Undefined and not null;
        int number(int index, int otherwise)
        {
            if (!given(index)) return otherwise;
            var value = toNumber(arguments[index]);
            return double.IsNaN(value) ? 0 : (int)Math.Clamp(Math.Truncate(value), int.MinValue / 2, int.MaxValue / 2);
        }
        int fromEnd(int value) => value < 0 ? Math.Max(0, text.Length + value) : Math.Min(value, text.Length);
        switch (name)
        {
            case "substr":
            {
                var start = fromEnd(number(0, 0));
                var length = Math.Clamp(number(1, text.Length - start), 0, text.Length - start);
                return text.Substring(start, length);
            }
            case "substring":
            {
                var (from, to) = (Math.Clamp(number(0, 0), 0, text.Length), Math.Clamp(number(1, text.Length), 0, text.Length));
                return from <= to ? text[from..to] : text[to..from];
            }
            case "slice":
            {
                var (from, to) = (fromEnd(number(0, 0)), fromEnd(number(1, text.Length)));
                return from < to ? text[from..to] : string.Empty;
            }
            case "indexOf":
            {
                var wanted = toAvmString(given(0) ? arguments[0] : Avm1Undefined.Instance);
                var from = Math.Clamp(number(1, 0), 0, text.Length);
                return (double)text.IndexOf(wanted, from, StringComparison.Ordinal);
            }
            case "lastIndexOf":
            {
                var wanted = toAvmString(given(0) ? arguments[0] : Avm1Undefined.Instance);
                var from = Math.Clamp(number(1, text.Length), 0, text.Length);
                var limit = Math.Min(text.Length, from + wanted.Length);
                return (double)text[..limit].LastIndexOf(wanted, StringComparison.Ordinal);
            }
            case "toUpperCase":
                return text.ToUpperInvariant();
            default:
                return text.ToLowerInvariant();
        }
    }

    Avm1Lookup IAvm1Host.CallMethod(Avm1ExecutionContext context, object? target, string name, IReadOnlyList<object?> arguments, int instructionOffset)
    {
        var instance = (DisplayInstance)context.Owner;
        // Branches whose lambdas would capture this method's parameters live in methods of their own: a captured
        // parameter makes C# allocate the closure on every call, and this runs for every script call.
        if (ReferenceEquals(target, _externalInterface) && name == "addCallback")
            return addCallback(context, instance, arguments);
        if (name == "toString" && target is null or bool or double or string or Avm1Undefined)
            return new Avm1Lookup(true, toAvmString(target));
        if (target is string text && name == "charAt")
        {
            var index = arguments.Count > 0 ? (int)toNumber(arguments[0]) : 0;
            return new Avm1Lookup(
                true,
                (uint)index < (uint)text.Length ? text[index].ToString() : string.Empty);
        }
        if (target is string stringTarget && name is "substr" or "substring" or "slice" or "indexOf" or "lastIndexOf" or "toUpperCase" or "toLowerCase")
            return new Avm1Lookup(true, stringMethod(stringTarget, name, arguments));
        if (target is string codeText && name == "charCodeAt")
        {
            var index = arguments.Count > 0 ? (int)toNumber(arguments[0]) : 0;
            return new Avm1Lookup(
                true,
                (uint)index < (uint)codeText.Length ? (double)codeText[index] : double.NaN);
        }
        if (target is Avm1ArrayObject array && name == "push")
        {
            var lengthLookup = context.GetMember(array, "length");
            var length = lengthLookup.Found ? toNumber(lengthLookup.Value) : 0;
            if (!double.IsFinite(length)
                || length < 0
                || length != Math.Truncate(length)
                || length > _limits.MaxStackValues - arguments.Count)
            {
                return new Avm1Lookup(true, double.NaN);
            }

            var start = (int)length;
            for (var index = 0; index < arguments.Count; index++)
                context.SetMember(array, (start + index).ToString(System.Globalization.CultureInfo.InvariantCulture), arguments[index]);
            var newLength = (double)(start + arguments.Count);
            context.SetMember(array, "length", newLength);
            return new Avm1Lookup(true, newLength);
        }
        if (target is Avm1ArrayObject listArray && name is "pop" or "shift" or "join" or "reverse")
            return listMethod(context, listArray, name, arguments);
        if (target is MovieClipLoaderObject loader && name == "addListener"
            && arguments.Count > 0 && arguments[0] is Avm1Object listener)
        {
            loader.Listeners.Add(listener);
            return new Avm1Lookup(true, true);
        }
        if (target is MovieClipLoaderObject clipLoader && name == "loadClip" && arguments.Count >= 2
            && arguments[0] is string url && arguments[1] is DisplayInstance loadTarget)
        {
            if (LumenEdgeLog.Enabled)
                edge("load", url);
            if (MovieLoader?.Invoke(url) is not { } loaded)
            {
                reportOnce("LUM_AVM_LOADCLIP_UNRESOLVED", instance.CharacterId, instance.Frame,
                    $"MovieClipLoader.loadClip could not resolve '{url}'.");
                return new Avm1Lookup(true, false);
            }
            loadTarget.LoadedMovie = loaded;
            _loadedClips.Add(loadTarget);
            foreach (var each in clipLoader.Listeners)
                _pendingLoadInits.Add((each, loadTarget));
            return new Avm1Lookup(true, true);
        }
        if (target is DisplayInstance { LoadedMovie: { } loadedMovie } && name.Length != 0
            && context.GetMember(target, name).Value is not (Avm1FunctionValue or Avm1NativeFunction or Avm1MethodWrapper))
        {
            var forwarded = callLoadedMovie(loadedMovie.Player, name, arguments);
            if (forwarded.Found)
                return forwarded;
        }
        if (target is DisplayInstance depthTarget && name == "getNextHighestDepth")
        {
            var nextDepth = context.GetNextHighestDepth(depthTarget, depthTarget.Children.Keys);
            return new Avm1Lookup(true, (double)nextDepth);
        }
        if (target is DisplayInstance depthInstance && name == "getDepth")
            return new Avm1Lookup(true, (double)depthInstance.Depth);
        if (target is DisplayInstance swapInstance
            && name == "swapDepths"
            && arguments.Count > 0)
        {
            var destination = arguments[0] is DisplayInstance other
                ? other.Depth
                : tryAvmDepth(arguments[0], out var numericDepth)
                    ? numericDepth
                    : long.MinValue;
            if (destination != long.MinValue)
                swapDepth(swapInstance, destination);
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        }
        if (target is DisplayInstance removeInstance && name == "removeMovieClip")
        {
            scheduleRemoval(context, removeInstance);
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        }
        if (target is DisplayInstance emptyParent
            && name == "createEmptyMovieClip"
            && arguments.Count >= 2
            && arguments[0] is string emptyName
            && tryAvmDepth(arguments[1], out var emptyDepth))
        {
            var empty = new DisplayInstance(uint.MaxValue, emptyParent.HierarchyDepth + 1, emptyParent)
            {
                Name = emptyName,
                Depth = emptyDepth,
            };
            context.SetTransientMember(emptyParent, emptyName, empty);
            context.SetTransientChild(emptyParent, emptyDepth, empty);
            var emptyAt = emptyParent; // a block local: capturing the pattern variable allocated the closure on every call
            context.OnCommit(() => placeScriptChild(emptyAt, empty));
            return new Avm1Lookup(true, empty);
        }
        if (target is DisplayInstance attachTarget
            && name == "attachMovie"
            && arguments.Count >= 3
            && arguments[0] is string linkageName
            && arguments[1] is string instanceName
            && tryAvmDepth(arguments[2], out var attachDepth))
        {
            var linkage = linkageName;
            var exported = _movie.Sprites
                .Select(sprite => _sprites[sprite.CharacterId])
                .FirstOrDefault(timeline => timeline.ExportName == linkage);
            if (exported.ExportName is null)
            {
                reportOnce(
                    "LUM_AVM_ATTACH_EXPORT_NOT_FOUND",
                    instance.CharacterId,
                    instance.Frame,
                    $"MovieClip.attachMovie could not resolve exported symbol '{linkageName}'.");
                return new Avm1Lookup(true, Avm1Undefined.Instance);
            }

            var attached = new DisplayInstance(
                exported.CharacterId,
                attachTarget.HierarchyDepth + 1,
                attachTarget)
            {
                Name = instanceName,
                Depth = attachDepth,
            };
            if (arguments.Count >= 4 && arguments[3] is Avm1Object initialValues)
            {
                foreach (var (propertyName, propertyValue) in initialValues.Properties)
                    writeMember(attached, propertyName, propertyValue);
            }
            enterFrame(attached, 0, queueActions: true);
            if (_classes.TryGetValue(linkageName, out var attachedClass))
                constructInstance(attached, attachedClass);
            context.SetTransientMember(attachTarget, instanceName, attached);
            context.SetTransientChild(attachTarget, attachDepth, attached);
            var attachAt = attachTarget;
            context.OnCommit(() => placeScriptChild(attachAt, attached));
            return new Avm1Lookup(true, attached);
        }
        // A copy of the clip beside it (same parent): its symbol at its first frame, its colour, blend and class
        // (reward_shop's reward sliders). Unlike Flash, the copy starts at the parent's origin, not at the original's
        // place: Green's sliders sit there (the template is authored at y -90, then SetAndStart moves the copy up 90;
        // with Flash's copied transform they end a row too high, under the title; checked against Green in
        // TaikoRecomp, session-rewards-7).
        if (target is DisplayInstance original
            && name == "duplicateMovieClip"
            && original.Parent is { } duplicateParent
            && arguments.Count >= 2
            && arguments[0] is string duplicateName
            && tryAvmDepth(arguments[1], out var duplicateDepth))
        {
            var duplicate = new DisplayInstance(original.CharacterId, original.HierarchyDepth, duplicateParent)
            {
                Name = duplicateName,
                Depth = duplicateDepth,
                Color = original.Color,
                BlendMode = original.BlendMode,
                Visible = original.Visible,
                ScriptTransformed = true,
            };
            if (arguments.Count >= 3 && arguments[2] is Avm1Object duplicateValues)
            {
                foreach (var (propertyName, propertyValue) in duplicateValues.Properties)
                    writeMember(duplicate, propertyName, propertyValue);
            }
            enterFrame(duplicate, 0, queueActions: true);
            if (original.ConstructedClass is { } duplicateClass)
                constructInstance(duplicate, duplicateClass);
            context.SetTransientMember(duplicateParent, duplicateName, duplicate);
            context.SetTransientChild(duplicateParent, duplicateDepth, duplicate);
            var duplicateAt = duplicateParent;
            context.OnCommit(() => placeScriptChild(duplicateAt, duplicate));
            return new Avm1Lookup(true, duplicate);
        }
        if (target is DisplayInstance targetInstance && name is "play" or "stop")
        {
            if (ReferenceEquals(targetInstance, instance))
                context.PendingPlayback = name == "play";
            targetInstance.Playing = name == "play";
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        }
        if (target is DisplayInstance jumpTarget
            && name is "gotoAndPlay" or "gotoAndStop"
            && arguments.Count > 0)
        {
            if (tryResolveTimelineFrame(jumpTarget, arguments[0], out var targetFrame))
            {
                if (ReferenceEquals(jumpTarget, instance))
                    context.PendingPlayback = name == "gotoAndPlay";
                jumpInstance(jumpTarget, targetFrame, name == "gotoAndPlay");
            }
            // Some movies jump to a missing label every frame: the message (every label, sorted) only the first time.
            else if (!_diagnosticKeys.Contains(("LUM_AVM_GOTO_INVALID", instance.CharacterId, instance.Frame, jumpTarget.Name)))
                reportOnce(
                    "LUM_AVM_GOTO_INVALID",
                    instance.CharacterId,
                    instance.Frame,
                    $"Movie clip method '{name}' on character {jumpTarget.CharacterId} "
                    + $"instance '{jumpTarget.Name}' could not resolve target '{toAvmString(arguments[0])}'; "
                    + $"available labels: {string.Join(", ", _sprites[jumpTarget.CharacterId].Labels.Keys.Order(StringComparer.Ordinal))}.",
                    jumpTarget.Name, format: false);
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
            var (classExport, classFunction) = (exportName, registeredClass);
            context.OnCommit(() => registerClass(classExport, classFunction));
            return new Avm1Lookup(true, true);
        }
        var receiver = target is Avm1SuperValue super ? super.ThisValue : target;
        var candidate = name.Length == 0
            ? target is Avm1SuperValue superTarget
                ? superTarget.Level?.GetProperty("__constructor__") ?? default
                : new Avm1Lookup(target is Avm1FunctionValue, target)
            : context.GetMember(target, name);
        if (candidate.Found && candidate.Value is Avm1FunctionValue function)
            return invokeFunction(instance, function, receiver, arguments, context);
        if (candidate.Found && candidate.Value is Avm1MethodWrapper wrapper)
            return wrapper.Invoke(receiver, arguments);
        if (candidate.Found && candidate.Value is Avm1NativeFunction nativeFunction)
            return invokeHost(instance, nativeFunction, arguments);
        if (target is Avm1SuperValue && name.Length == 0)
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        reportOnce(
            "LUM_AVM_METHOD_UNRESOLVED",
            instance.CharacterId,
            instance.Frame,
            $"AVM method '{name}' at action offset 0x{instructionOffset:X} is not registered on {describeAvmType(target)}; undefined was returned.");
        return default;
    }

    Avm1Lookup IAvm1Host.ConstructObject(Avm1ExecutionContext context, string name, IReadOnlyList<object?> arguments)
    {
        var instance = (DisplayInstance)context.Owner;
        var candidate = context.GetVariable(name);
        Avm1Object value;
        if (name == "Array")
        {
            if (arguments.Count == 1
                && toNumber(arguments[0]) is var requestedLength
                && double.IsFinite(requestedLength)
                && requestedLength >= 0
                && requestedLength == Math.Truncate(requestedLength)
                && requestedLength <= _limits.MaxStackValues)
            {
                value = new Avm1ArrayObject(
                    Enumerable.Repeat<object?>(Avm1Undefined.Instance, (int)requestedLength).ToArray());
            }
            else
                value = new Avm1ArrayObject(arguments);
        }
        else if (candidate.Value is MovieClipLoaderConstructor)
            return new Avm1Lookup(true, new MovieClipLoaderObject());
        else if (candidate.Value is DateConstructor)
            return new Avm1Lookup(true, createDate(arguments));
        else
            value = new Avm1Object();
        value.Properties["__constructor__"] = name;
        value.Properties["__arguments__"] = new Avm1ArrayObject(arguments);
        if (candidate.Found && candidate.Value is Avm1FunctionValue constructor)
        {
            value.Prototype = constructor.GetProperty("prototype").Value as Avm1Object;
            if (!invokeFunction(instance, constructor, value, arguments, context).Found)
                return default;
        }
        return new Avm1Lookup(true, value);
    }

    void IAvm1Host.GotoLabel(Avm1ExecutionContext context, string label)
    {
        var instance = (DisplayInstance)context.Owner;
        var timeline = _sprites[instance.CharacterId];
        if (timeline.Labels.TryGetValue(label, out var frame))
            jumpInstance(instance, frame, context.PendingPlayback ?? instance.Playing);
        else
            reportOnce(
                "LUM_AVM_GOTO_INVALID",
                instance.CharacterId,
                instance.Frame,
                $"Timeline action could not resolve label '{label}'.");
    }

    Avm1Lookup IAvm1Host.CloneSprite(Avm1ExecutionContext context, object? source, string name, int depth)
    {
        if (source is not DisplayInstance sourceInstance
            || sourceInstance.Parent is not DisplayInstance parent
            || depth < 0)
            return default;
        var clone = cloneInstance(sourceInstance, parent, name);
        clone.Depth = depth;
        context.SetTransientMember(parent, name, clone);
        context.SetTransientChild(parent, depth, clone);
        context.OnCommit(() =>
        {
            var targetDepth = (long)depth;
            if (parent.Children.TryGetValue(targetDepth, out var replaced))
                replaced.Removed = true;
            parent.Children[targetDepth] = clone;
        });
        return new Avm1Lookup(true, clone);
    }

    void IAvm1Host.RemoveSprite(Avm1ExecutionContext context, object? target)
    {
        if (target is not DisplayInstance targetInstance
            || targetInstance.Parent is null)
            return;
        scheduleRemoval(context, targetInstance);
    }

    void IAvm1Host.GotoFrame(Avm1ExecutionContext context, object? value, bool play, int bias)
    {
        var instance = (DisplayInstance)context.Owner;
        var target = instance;
        if (value is string pathAndFrame && pathAndFrame.LastIndexOf(':') is var separator && separator >= 0)
        {
            var path = pathAndFrame[..separator];
            target = path.StartsWith('/') ? _root : instance;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                target = part is ".." or "_parent" ? target?.Parent
                    : target?.Children.Values.FirstOrDefault(child => !child.Removed && child.Name == part);
                if (target is null)
                    return;
            }
            value = pathAndFrame[(separator + 1)..];
        }
        if (_sprites.TryGetValue(target.CharacterId, out var targetTimeline)
            && tryResolveTimelineFrame(target, value, out var frame)
            && frame + bias < targetTimeline.Frames.Length)
        {
            if (ReferenceEquals(target, instance))
                context.PendingPlayback = play;
            jumpInstance(target, frame + bias, play);
        }
    }

    void IAvm1Host.HostCommand(Avm1ExecutionContext context, string command, string argument)
    {
        if (LumenEdgeLog.Enabled)
            edge("command", $"{command} {argument}");
        HostCommand?.Invoke(command, argument);
    }

    Avm1Lookup IAvm1Host.ConstructMethod(Avm1ExecutionContext context, object? target, string name, IReadOnlyList<object?> arguments)
    {
        var instance = (DisplayInstance)context.Owner;
        var candidate = string.IsNullOrEmpty(name) ? new Avm1Lookup(true, target) : context.GetMember(target, name);
        if (candidate.Value is ColorTransformConstructor)
            return new Avm1Lookup(true, createColorTransform(arguments));
        if (candidate.Value is not Avm1FunctionValue constructor)
        {
            reportOnce("LUM_AVM_CONSTRUCTOR_UNRESOLVED", instance.CharacterId, instance.Frame,
                $"AVM constructor '{name}' is not registered.");
            return default;
        }
        var value = new Avm1Object { Prototype = constructor.GetProperty("prototype").Value as Avm1Object };
        value.Properties["__constructor__"] = constructor;
        return invokeFunction(instance, constructor, value, arguments, context).Found
            ? new Avm1Lookup(true, value) : default;
    }

    private static void placeScriptChild(DisplayInstance parent, DisplayInstance child)
    {
        // A swapDepths in the same action already put the new clip there (reward_shop's goods icons).
        if (parent.Children.TryGetValue(child.Depth, out var replaced) && !ReferenceEquals(replaced, child))
            replaced.Removed = true;
        parent.Children[child.Depth] = child;
    }

    private static void scheduleRemoval(Avm1ExecutionContext context, DisplayInstance instance)
    {
        if (instance.Parent is not { } parent)
            return;
        context.SetTransientChild(parent, instance.Depth, null);
        if (instance.Name is { Length: > 0 } name)
            context.SetTransientMember(parent, name, Avm1Undefined.Instance);
        context.OnCommit(() =>
        {
            if (parent.Children.TryGetValue(instance.Depth, out var current)
                && ReferenceEquals(current, instance))
            {
                parent.Children.Remove(instance.Depth);
            }
            instance.Removed = true;
        });
    }

    private static DisplayInstance cloneInstance(
        DisplayInstance source,
        DisplayInstance parent,
        string name)
    {
        var clone = new DisplayInstance(source.CharacterId, parent.HierarchyDepth + 1, parent)
        {
            Name = name,
            Depth = source.Depth,
            PlacementId = source.PlacementId,
            FirstFrame = source.FirstFrame,
            Frame = source.Frame,
            Playing = source.Playing,
            Visible = source.Visible,
            BlendMode = source.BlendMode,
            Transform = source.Transform,
            Color = source.Color,
            PreviousTransform = source.PreviousTransform,
            PreviousMultiply = source.PreviousMultiply,
            PreviousVisible = source.PreviousVisible,
            ScriptTransformed = source.ScriptTransformed,
            ScriptColored = source.ScriptColored,
            ClipDepth = source.ClipDepth,
            ScriptPrototype = source.ScriptPrototype,
            ConstructedClass = source.ConstructedClass,
        };
        foreach (var (variableName, value) in source.Variables)
            clone.Variables.Add(variableName, value);
        foreach (var (depth, child) in source.Children)
            clone.Children.Add(depth, cloneInstance(child, clone, child.Name));
        return clone;
    }

    private static void swapDepth(DisplayInstance instance, long destination)
    {
        if (instance.Parent is not { } parent)
            return;
        var source = instance.Depth;
        if (source == destination)
            return;
        parent.Children.Remove(source);
        if (parent.Children.Remove(destination, out var displaced))
        {
            displaced.Depth = source;
            parent.Children[source] = displaced;
        }
        instance.Depth = destination;
        parent.Children[destination] = instance;
        // Flash: a clip moved by script is the script's now, and a rewinding timeline leaves it alone.
        instance.FromTimeline = false;
    }

    /// <summary>Diagnostic: WADDAMBURO_HOST_TRACE=1 prints every movie -> host call with its result.</summary>
    private static readonly bool TraceHostCalls = DiagnosticSwitches.HostTrace;

    private static string describe(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"'{value.AsString()}'",
        _ => value.Kind.ToString().ToLowerInvariant(),
    };

    private Avm1Lookup invokeHost(
        DisplayInstance instance,
        Avm1NativeFunction function,
        IReadOnlyList<object?> arguments)
    {
        try
        {
            var converted = arguments.Select(toHostValue).ToImmutableArray();
            var result = function.Callback(new LumenHostCall(converted));
            if (function.Host && LumenEdgeLog.Enabled)
                edge("out", $"{function.Name}({describe(converted)}) -> {describe(result)}");
            if (TraceHostCalls)
                Console.Error.WriteLine($"[host] {function.Name}({string.Join(", ", converted.Select(describe))}) -> {describe(result)}");
            return new Avm1Lookup(true, fromHostValue(result));
        }
        catch (Exception exception)
        {
            // Printed at once too: the movie carries on, so a failed handoff otherwise looks like a hang.
            Console.Error.WriteLine($"Host call '{function.Name}' failed: {exception.GetType().Name}: {exception.Message}");
            reportOnce(
                "LUM_HOST_CALL_FAILED",
                instance.CharacterId,
                instance.Frame,
                $"Host call '{function.Name}' failed with {exception.GetType().Name}: {exception.Message} Undefined was returned.");
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
        Avm1Object script => LumenHostValue.FromObject(script.Properties
            .Where(static field => field.Value is null or bool or double or string)
            .ToDictionary(static field => field.Key, static field => toHostValue(field.Value), StringComparer.Ordinal)),
        _ => LumenHostValue.Undefined,
    };

    private static object? fromHostValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => Avm1Undefined.Instance,
        LumenHostValueKind.Null => null,
        LumenHostValueKind.Boolean => value.AsBoolean(),
        LumenHostValueKind.Number => value.AsNumber(),
        LumenHostValueKind.Text => value.AsString(),
        LumenHostValueKind.Fields => hostFields(value),
        _ => Avm1Undefined.Instance,
    };

    private static Avm1Object hostFields(LumenHostValue value)
    {
        var result = new Avm1Object();
        foreach (var field in value.AsObject()) result.Properties[field.Key] = fromHostValue(field.Value);
        return result;
    }

    private Avm1Lookup invokeFunction(
        DisplayInstance timelineTarget,
        Avm1FunctionValue function,
        object? thisValue,
        IReadOnlyList<object?> arguments,
        Avm1ExecutionContext? parentContext = null)
    {
        if (!_scriptsEnabled) return default;
        if (function.DefinitionTarget is DisplayInstance definitionTarget)
            timelineTarget = definitionTarget;
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
            var startingFrame = timelineTarget.Frame;
            var startingPlaying = timelineTarget.Playing;
            var context = createExecutionContext(
                timelineTarget,
                parentContext,
                new Avm1LexicalScope(function.CapturedScope),
                thisValue,
                timelineTarget);
            var previousContext = _activeContext;
            _activeContext = context;
            Avm1ExecutionStatus status;
            bool playing;
            object? returnValue;
            try
            {
                status = Avm1Interpreter.TryExecuteFunction(
                    function,
                    _movie.Strings,
                    thisValue,
                    arguments,
                    timelineTarget.Playing,
                    _limits,
                    context,
                    out playing,
                    out returnValue);
            }
            finally
            {
                _activeContext = previousContext;
            }
            if (status != Avm1ExecutionStatus.Success)
            {
                var unsupported = status == Avm1ExecutionStatus.Unsupported
                    ? Avm1Interpreter.DescribeUnsupported(function.Body)
                    : string.Empty;
                reportOnce(
                    "LUM_AVM_FUNCTION_DEFERRED",
                    timelineTarget.CharacterId,
                    timelineTarget.Frame,
                    $"AVM function body requires unsupported semantics or failed with {status}"
                    + (unsupported.Length == 0 ? "." : $": {unsupported}."));
                return default;
            }
            if (context.PendingPlayback is { } requestedPlayback)
                timelineTarget.Playing = requestedPlayback;
            else if (timelineTarget.Frame == startingFrame && timelineTarget.Playing == startingPlaying)
                timelineTarget.Playing = playing;
            context.Commit();
            if (context.PendingPlayback is { } nestedPlayback
                && parentContext is not null && ReferenceEquals(parentContext.TimelineTarget, timelineTarget))
                parentContext.PendingPlayback = nestedPlayback;
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
            queueConstruction(instance, function);
        }
        foreach (var child in childrenOf(instance))
            bindClass(child, exportName, function);
    }

    private void constructInstance(DisplayInstance instance, Avm1FunctionValue function)
    {
        instance.ScriptPrototype = function.GetProperty("prototype").Value as Avm1Object;
        var result = invokeFunction(instance, function, instance, []);
        if (result.Found)
        {
            instance.ConstructedClass = function;
            var onLoad = readMember(instance, "onLoad");
            if (onLoad.Found && onLoad.Value is Avm1FunctionValue onLoadFunction)
                _ = invokeFunction(instance, onLoadFunction, instance, []);
        }
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
            var outer = _gotoBorn;
            // Only a rewind: on a forward goto song_select's courseStar_ (Startup("course")) and the course boards
            // would run theirs and back-out from the courses then hangs; the game never runs those.
            List<DisplayInstance> born = [];
            _gotoBorn = frame > instance.Frame ? null : born;
            try
            {
                if (frame > instance.Frame)
                {
                    for (var nextFrame = instance.Frame + 1; nextFrame <= frame; nextFrame++)
                        enterFrame(instance, nextFrame, queueActions: nextFrame == frame);
                }
                else
                {
                    rewindTimeline(instance, frame, queueActions: true);
                }
            }
            finally
            {
                _gotoBorn = outer;
            }
            // A clip a rewind creates on a frame it passes through still runs its first frame's script when it
            // survives to the target frame (ai_hit_effect's mc_don_position, placed at frame 0 of mc_don_move: its
            // stop() keeps it from playing on into don_clash after a section win).
            foreach (var child in born)
            {
                var alive = true;
                for (var node = child; node is not null && node != instance; node = node.Parent)
                    alive &= !node.Removed;
                // A class-bound clip is set up by its constructor: song_select's courseStar_ and modeTitle_, re-created
                // when backing out of the courses rewinds main, would rerun Startup("course") and hang the menu.
                // ponytail: class-bound vs plain is the observed split, not a known rule of the game's player.
                if (!alive || child.Frame < 0 || child.ConstructedClass is not null
                    || _pendingConstructions.Exists(pending => pending.Instance == child)
                    || !_sprites.TryGetValue(child.CharacterId, out var childTimeline))
                    continue;
                foreach (var action in childTimeline.Frames[child.Frame].OfKind(LmbCommandKind.DoAction))
                    enqueueAction(child, action);
            }
            resetInterpolation(instance);
        }
        instance.Playing = play;
    }

    private Avm1Lookup readMember(object? target, string name)
    {
        if (target is ClipTransform transform && name == "colorTransform")
            return new Avm1Lookup(true, createColorTransform([
                (double)transform.Instance.Color.Multiply.Red, (double)transform.Instance.Color.Multiply.Green,
                (double)transform.Instance.Color.Multiply.Blue, (double)transform.Instance.Color.Multiply.Alpha,
                (double)transform.Instance.Color.Add.Red * 255, (double)transform.Instance.Color.Add.Green * 255,
                (double)transform.Instance.Color.Add.Blue * 255, (double)transform.Instance.Color.Add.Alpha * 255]));
        if (target is Avm1SuperValue super
            && super.Level?.Prototype is Avm1Object parentPrototype)
        {
            return parentPrototype.GetProperty(name);
        }
        if (target is DisplayInstance instance)
        {
            // Every member read on a clip checks its children first: an index loop, no closure or enumerator.
            var children = instance.Children.Values;
            for (var index = 0; index < children.Count; index++)
                if (children[index] is { Removed: false } child && child.Name == name)
                    return new Avm1Lookup(true, child);
            if (instance.Variables.TryGetValue(name, out var value))
                return new Avm1Lookup(true, value);
            if (instance.ScriptPrototype?.GetProperty(name) is { Found: true } inherited)
                return inherited;
            return name switch
            {
                "transform" => new Avm1Lookup(true, new ClipTransform(instance)),
                "_parent" => new Avm1Lookup(instance.Parent is not null, instance.Parent),
                "_root" => new Avm1Lookup(true, rootOf(instance)),
                "_name" => new Avm1Lookup(true, instance.Name),
                "_visible" => new Avm1Lookup(true, instance.Visible),
                "_x" => new Avm1Lookup(true, (double)instance.Transform.X),
                "_y" => new Avm1Lookup(true, (double)instance.Transform.Y),
                "_xscale" => new Avm1Lookup(true, decompose(instance.Transform).ScaleX * 100d),
                "_yscale" => new Avm1Lookup(true, decompose(instance.Transform).ScaleY * 100d),
                "_rotation" => new Avm1Lookup(true, decompose(instance.Transform).Rotation * 180d / Math.PI),
                "_alpha" => new Avm1Lookup(true, instance.Color.Multiply.Alpha * 100d),
                "_currentframe" => new Avm1Lookup(true, (double)instance.Frame + 1),
                "_totalframes" => new Avm1Lookup(true, (double)(_sprites.TryGetValue(instance.CharacterId, out var sprite)
                    ? sprite.Frames.Length : 1)),
                "__proto__" => new Avm1Lookup(instance.ScriptPrototype is not null, instance.ScriptPrototype),
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
            return name == "__proto__"
                ? new Avm1Lookup(avmObject.Prototype is not null, avmObject.Prototype)
                : avmObject.GetProperty(name);
        if (target is Dictionary<string, object?> dictionary && dictionary.TryGetValue(name, out var member))
            return new Avm1Lookup(true, member);
        return default;
    }

    private static DisplayInstance rootOf(DisplayInstance instance)
    {
        while (instance.Parent is not null)
            instance = instance.Parent;
        return instance;
    }

    private void writeMember(object? target, string name, object? value)
    {
        if (target is ClipTransform transform && name == "colorTransform" && value is Avm1Object color)
        {
            float channel(string property, double fallback, double divisor = 1)
            {
                var entry = color.GetProperty(property);
                var number = entry.Found ? toNumber(entry.Value) : fallback;
                return double.IsFinite(number) ? (float)Math.Clamp(number / divisor, -1e6, 1e6) : (float)fallback;
            }
            transform.Instance.ScriptColored = true;
            transform.Instance.Color = new ColorState(
                new(channel("redMultiplier", 1), channel("greenMultiplier", 1), channel("blueMultiplier", 1), channel("alphaMultiplier", 1)),
                new(channel("redOffset", 0, 255), channel("greenOffset", 0, 255), channel("blueOffset", 0, 255), channel("alphaOffset", 0, 255)));
            return;
        }
        if (target is DisplayInstance instance)
        {
            switch (name)
            {
                case "_name":
                    instance.Name = toAvmString(value);
                    return;
                case "_visible":
                    instance.Visible = toBoolean(value);
                    return;
                case "_x":
                    if (tryDisplayNumber(value, out var x))
                    {
                        instance.ScriptTransformed = true;
                        instance.Transform = instance.Transform with { X = (float)x };
                    }
                    return;
                case "_y":
                    if (tryDisplayNumber(value, out var y))
                    {
                        instance.ScriptTransformed = true;
                        instance.Transform = instance.Transform with { Y = (float)y };
                    }
                    return;
                case "_xscale":
                    if (tryDisplayNumber(value, out var scaleX))
                    {
                        instance.ScriptTransformed = true;
                        var parts = decompose(instance.Transform);
                        instance.Transform = compose(instance.Transform, scaleX / 100d, parts.ScaleY, parts.Rotation);
                    }
                    return;
                case "_yscale":
                    if (tryDisplayNumber(value, out var scaleY))
                    {
                        instance.ScriptTransformed = true;
                        var parts = decompose(instance.Transform);
                        instance.Transform = compose(instance.Transform, parts.ScaleX, scaleY / 100d, parts.Rotation);
                    }
                    return;
                case "_rotation":
                    if (tryDisplayNumber(value, out var rotation))
                    {
                        instance.ScriptTransformed = true;
                        var parts = decompose(instance.Transform);
                        instance.Transform = compose(instance.Transform, parts.ScaleX, parts.ScaleY, rotation * Math.PI / 180d);
                    }
                    return;
                case "_alpha":
                    if (tryDisplayNumber(value, out var alpha))
                    {
                        instance.ScriptTransformed = true;
                        instance.Color = instance.Color with
                        {
                            Multiply = instance.Color.Multiply with { Alpha = (float)(alpha / 100d) },
                        };
                    }
                    return;
                default:
                    instance.Variables[name] = value;
                    return;
            }
        }
        if (target is Avm1ArrayObject array
            && array.TrySetIndexedProperty(name, value, _limits.MaxStackValues))
        {
            array.Properties.Remove(name);
            return;
        }
        if (target is Avm1Object avmObject)
        {
            if (name == "__proto__" && value is Avm1Object prototype)
                avmObject.Prototype = prototype;
            else
            {
                avmObject.Properties[name] = value;
                if (value is Avm1FunctionValue function)
                    function.OwnerPrototype = avmObject;
            }
            return;
        }
        if (target is Dictionary<string, object?> dictionary)
            dictionary[name] = value;
    }

    private static bool tryDisplayNumber(object? value, out double number)
    {
        if (value is null or Avm1Undefined)
        {
            number = 0;
            return false;
        }
        number = toNumber(value);
        return double.IsFinite(number);
    }

    private static (double ScaleX, double ScaleY, double Rotation) decompose(LumenMatrix matrix) =>
        (Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12),
            Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22),
            Math.Atan2(matrix.M12, matrix.M11));

    private static LumenMatrix compose(
        LumenMatrix current,
        double scaleX,
        double scaleY,
        double rotation) =>
        new(
            (float)(scaleX * Math.Cos(rotation)),
            (float)(scaleX * Math.Sin(rotation)),
            (float)(-scaleY * Math.Sin(rotation)),
            (float)(scaleY * Math.Cos(rotation)),
            current.X,
            current.Y);

    private static Avm1Object createBuiltinConstructor()
    {
        var constructor = new Avm1Object();
        constructor.Properties["prototype"] = new Avm1Object();
        return constructor;
    }

    // While recording, every run draws the same numbers so two runs of a session compare line by line.
    private static readonly Random EdgeRandom = LumenEdgeLog.Enabled ? new Random(1) : Random.Shared;

    private sealed record ClipTransform(DisplayInstance Instance);
    private sealed class ColorTransformConstructor;
    private sealed class MovieClipLoaderConstructor;
    private sealed class DateConstructor;
    private sealed class MovieClipLoaderObject : Avm1Object
    {
        public List<Avm1Object> Listeners { get; } = [];
    }
    private static readonly string[] ColorTransformProperties =
        ["redMultiplier", "greenMultiplier", "blueMultiplier", "alphaMultiplier",
         "redOffset", "greenOffset", "blueOffset", "alphaOffset"];

    private static Avm1Object createColorTransform(IReadOnlyList<object?> arguments)
    {
        var value = new Avm1Object();
        for (var index = 0; index < ColorTransformProperties.Length; index++)
            value.Properties[ColorTransformProperties[index]] = index < arguments.Count
                ? toNumber(arguments[index]) : index < 4 ? 1d : 0d;
        return value;
    }

    /// <summary>
    /// ActionScript 2's Date, the getters only, in local time: new Date() is now, new Date(ms) that many milliseconds
    /// after 1970 (UTC), new Date(year, month[, day, hours, minutes, seconds, ms]) that local time (month 0 to 11).
    /// ponytail: no setters, UTC getters or parse; add them when a movie needs them.
    /// </summary>
    private static Avm1Object createDate(IReadOnlyList<object?> arguments)
    {
        DateTime time;
        try
        {
            time = arguments.Count switch
            {
                0 => DateTime.Now,
                1 => DateTimeOffset.FromUnixTimeMilliseconds((long)toNumber(arguments[0])).LocalDateTime,
                _ => new DateTime((int)toNumber(arguments[0]), 1, 1, 0, 0, 0, DateTimeKind.Local)
                    .AddMonths((int)toNumber(arguments[1]))
                    .AddDays((arguments.Count > 2 ? toNumber(arguments[2]) : 1) - 1)
                    .AddHours(arguments.Count > 3 ? toNumber(arguments[3]) : 0)
                    .AddMinutes(arguments.Count > 4 ? toNumber(arguments[4]) : 0)
                    .AddSeconds(arguments.Count > 5 ? toNumber(arguments[5]) : 0)
                    .AddMilliseconds(arguments.Count > 6 ? toNumber(arguments[6]) : 0),
            };
        }
        catch (ArgumentException)
        {
            time = DateTime.MinValue; // out of range or not a number: Flash gives an invalid date; ours reads year 1
        }
        var date = new Avm1Object();
        void getter(string name, Func<double> value) => date.Properties[name] = new Avm1NativeFunction(
            $"Date.{name}", _ => LumenHostValue.FromNumber(value()));
        getter("getFullYear", () => time.Year);
        getter("getMonth", () => time.Month - 1);
        getter("getDate", () => time.Day);
        getter("getDay", () => (int)time.DayOfWeek);
        getter("getHours", () => time.Hour);
        getter("getMinutes", () => time.Minute);
        getter("getSeconds", () => time.Second);
        getter("getMilliseconds", () => time.Millisecond);
        getter("getTime", () => new DateTimeOffset(time).ToUnixTimeMilliseconds());
        getter("getTimezoneOffset", () => -TimeZoneInfo.Local.GetUtcOffset(time).TotalMinutes);
        return date;
    }

    private void installExternalInterface()
    {
        _externalInterface.Properties["available"] = true;
        _externalInterface.Properties["call"] = new Avm1NativeFunction(
            "ExternalInterface.call",
            _ => LumenHostValue.Undefined);
        var external = new Avm1Object();
        external.Properties["ExternalInterface"] = _externalInterface;
        var flash = new Avm1Object();
        flash.Properties["external"] = external;
        var geometry = new Avm1Object();
        geometry.Properties["ColorTransform"] = new ColorTransformConstructor();
        flash.Properties["geom"] = geometry;
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

    private void installMathObject()
    {
        var math = new Avm1Object();
        math.Properties["random"] = new Avm1NativeFunction(
            "Math.random", _ => LumenHostValue.FromNumber(EdgeRandom.NextDouble()));
        math.Properties["floor"] = new Avm1NativeFunction(
            "Math.floor",
            call => LumenHostValue.FromNumber(
                call.Arguments.IsEmpty ? double.NaN : Math.Floor(toHostNumber(call.Arguments[0]))));
        math.Properties["abs"] = new Avm1NativeFunction(
            "Math.abs",
            call => LumenHostValue.FromNumber(
                call.Arguments.IsEmpty ? double.NaN : Math.Abs(toHostNumber(call.Arguments[0]))));
        // The rest of the ActionScript 2 Math object (round is floor(x + 0.5), as in Flash).
        void unary(string name, Func<double, double> function) => math.Properties[name] = new Avm1NativeFunction(
            $"Math.{name}", call => LumenHostValue.FromNumber(
                call.Arguments.IsEmpty ? double.NaN : function(toHostNumber(call.Arguments[0]))));
        void binary(string name, Func<double, double, double> function) => math.Properties[name] = new Avm1NativeFunction(
            $"Math.{name}", call => LumenHostValue.FromNumber(call.Arguments.Length < 2
                ? double.NaN : function(toHostNumber(call.Arguments[0]), toHostNumber(call.Arguments[1]))));
        unary("ceil", Math.Ceiling);
        unary("round", value => Math.Floor(value + 0.5));
        unary("sqrt", Math.Sqrt);
        unary("sin", Math.Sin);
        unary("cos", Math.Cos);
        unary("tan", Math.Tan);
        unary("asin", Math.Asin);
        unary("acos", Math.Acos);
        unary("atan", Math.Atan);
        unary("exp", Math.Exp);
        unary("log", Math.Log);
        binary("atan2", Math.Atan2);
        binary("pow", Math.Pow);
        binary("min", Math.Min);
        binary("max", Math.Max);
        math.Properties["PI"] = Math.PI;
        math.Properties["E"] = Math.E;
        math.Properties["SQRT2"] = Math.Sqrt(2);
        math.Properties["LN2"] = Math.Log(2);
        math.Properties["LN10"] = Math.Log(10);
        _globals["Math"] = math;
    }

    private static double toHostNumber(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Null => 0,
        LumenHostValueKind.Boolean => value.AsBoolean() ? 1 : 0,
        LumenHostValueKind.Number => value.AsNumber(),
        LumenHostValueKind.Text => double.TryParse(
            value.AsString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) ? number : double.NaN,
        _ => double.NaN,
    };

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

    private static bool tryAvmDepth(object? value, out long depth)
    {
        var number = toNumber(value);
        if (double.IsFinite(number)
            && number >= int.MinValue
            && number <= int.MaxValue
            && number == Math.Truncate(number))
        {
            depth = (long)number;
            return true;
        }
        depth = 0;
        return false;
    }

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
        Avm1SuperValue => "a super object",
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
        LmbTimelineCommand placement,
        bool queueActions)
    {
        var depth = TimelineDepthBase + placement.Depth;
        parent.Children.TryGetValue(depth, out var instance);
        switch (placement.Mode)
        {
            case 1:
                if (parent.ReusableTimelineChildren is { } reusable
                    && reusable.TryGetValue(depth, out var reusableInstance)
                    && reusableInstance.CharacterId == placement.CharacterId
                    && reusableInstance.PlacementId == placement.PlacementId)
                {
                    reusable.Remove(depth);
                    reusableInstance.Removed = false;
                    parent.Children[depth] = reusableInstance;
                    applyPlacementFields(reusableInstance, placement, isNew: false);
                    break;
                }
                if (instance is not null)
                    instance.Removed = true;
                instance = new DisplayInstance(placement.CharacterId, parent.HierarchyDepth + 1, parent)
                {
                    Depth = depth,
                    PlacementId = placement.PlacementId,
                    FirstFrame = placement.FirstFrame,
                    FromTimeline = true,
                };
                parent.Children[depth] = instance;
                applyPlacementFields(instance, placement, isNew: true);
                if (!queueActions)
                    _gotoBorn?.Add(instance);
                if (_sprites.TryGetValue(instance.CharacterId, out var childTimeline))
                {
                    enterFrame(instance, 0, queueActions);
                    var exportName = childTimeline.ExportName;
                    if (exportName is not null && _classes.TryGetValue(exportName, out var registeredClass))
                        queueConstruction(instance, registeredClass);
                }
                else if (!_shapes.ContainsKey(instance.CharacterId) && !_texts.ContainsKey(instance.CharacterId))
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
                        Depth = depth,
                        Name = instance.Name,
                        PlacementId = placement.PlacementId,
                        FirstFrame = placement.FirstFrame,
                        FromTimeline = true,
                        Transform = instance.Transform,
                        Color = instance.Color,
                        BlendMode = instance.BlendMode,
                        ClipDepth = instance.ClipDepth,
                    };
                    parent.Children[depth] = instance = replacement;
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

    private void hostOffset(DisplayInstance instance)
    {
        if (_hostOffsets.TryGetValue(instance, out var offset))
            instance.Transform = instance.Transform with { X = instance.Transform.X + offset.X, Y = instance.Transform.Y + offset.Y };
    }

    private void applyPlacementFields(DisplayInstance instance, LmbTimelineCommand placement, bool isNew)
    {
        if (placement.NameStringIndex != 0 && placement.NameStringIndex < _movie.Strings.Length)
            instance.Name = _movie.Strings[checked((int)placement.NameStringIndex)].Value;
        if (isNew || placement.ClipDepth != 0)
            instance.ClipDepth = placement.ClipDepth == 0 ? null : TimelineDepthBase + placement.ClipDepth;
        if (isNew || placement.BlendMode != 0)
            instance.BlendMode = placement.BlendMode;
        if (instance.BlendMode > 3 && instance.BlendMode is not (4 or 8))
        {
            reportOnce(
                "LUM_BLEND_MODE_DEFERRED",
                instance.CharacterId,
                0,
                $"Blend mode {instance.BlendMode} is currently rendered as normal.");
        }
        if (instance.ScriptTransformed)
            return;

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
                hostOffset(instance);
                break;
            case 0x8000:
                if (placement.PositionIndex >= _movie.Translations.Length)
                {
                    reportOnce("LUM_TRANSLATION_NOT_FOUND", instance.CharacterId, 0, $"Translation {placement.PositionIndex} was left unchanged.");
                    break;
                }
                var translation = _movie.Translations[placement.PositionIndex];
                instance.Transform = LumenMatrix.Identity with { X = translation.X, Y = translation.Y };
                hostOffset(instance);
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

        if (instance.ScriptColored) return;
        var color = instance.Color;
        if (placement.ColorMultiplyIndex < _movie.ColorTransforms.Length)
            color = color with
            {
                Multiply = _colorOverrides.TryGetValue(placement.ColorMultiplyIndex, out var multiply) ? multiply
                    : convertColor(_movie.ColorTransforms[checked((int)placement.ColorMultiplyIndex)]),
            };
        else if (placement.ColorMultiplyIndex != uint.MaxValue)
            reportOnce("LUM_COLOR_NOT_FOUND", instance.CharacterId, 0, $"Multiply color {placement.ColorMultiplyIndex} was left unchanged.");
        else if (isNew)
            color = color with { Multiply = LumenRenderColor.White };
        if (placement.ColorAddIndex < _movie.ColorTransforms.Length)
            color = color with
            {
                Add = _colorOverrides.TryGetValue(placement.ColorAddIndex, out var add) ? add
                    : convertColor(_movie.ColorTransforms[checked((int)placement.ColorAddIndex)]),
            };
        else if (placement.ColorAddIndex != uint.MaxValue)
            reportOnce("LUM_COLOR_NOT_FOUND", instance.CharacterId, 0, $"Add color {placement.ColorAddIndex} was left unchanged.");
        else if (isNew)
            color = color with { Add = LumenRenderColor.Transparent };
        instance.Color = color;
    }

    private Rendering.LumenAnchor? anchorOf(DisplayInstance instance) =>
        ClipAnchors is { Count: > 0 } anchors && instance.Parent is { } holder
            && anchors.TryGetValue((holder.CharacterId, instance.Depth - TimelineDepthBase), out var anchor) ? anchor : null;

    // A stretched clip (a package's "anchor": "stretch") is drawn as it is, then its drawing is widened: each side to its
    // edge of the stage, a 9-sliced shape's corners and caps moved, not widened.
    private void appendInstance(
        DisplayInstance instance,
        LumenMatrix parentTransform,
        ColorState parentColor,
        LumenRenderBlend parentBlend,
        float interpolationFraction,
        ImmutableArray<LumenRenderQuad>.Builder quads,
        byte maskDepth = 0)
    {
        // To an edge (stretch-right, stretch-left) at any width; keeping its margins (stretch) only on another one.
        if (anchorOf(instance) is not { } stretched || !Rendering.LumenStage.Stretches(stretched)
            || stretched == Rendering.LumenAnchor.Stretch && Rendering.LumenStage.Width == Rendering.LumenStage.BaseWidth)
        {
            appendInstanceDrawn(instance, parentTransform, parentColor, parentBlend, interpolationFraction, quads, maskDepth);
            return;
        }
        var start = quads.Count;
        appendInstanceDrawn(instance, parentTransform, parentColor, parentBlend, interpolationFraction, quads, maskDepth);
        stretch(quads, start, stretched);
    }

    private void stretch(ImmutableArray<LumenRenderQuad>.Builder quads, int start, Rendering.LumenAnchor kind)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        for (var index = start; index < quads.Count; index++)
            foreach (var vertex in (ReadOnlySpan<LumenRenderVertex>)[quads[index].TopLeft, quads[index].TopRight, quads[index].BottomRight, quads[index].BottomLeft])
                (minX, maxX) = (MathF.Min(minX, vertex.X), MathF.Max(maxX, vertex.X));
        if (!(maxX > minX))
            return;
        var (from, to) = Rendering.LumenStage.StretchSpan(minX, maxX, SceneAnchor, kind, SceneScale, SceneX);
        var scale = (to - from) / (maxX - minX);
        float map(float x) => from + (x - minX) * scale;
        static LumenRenderVertex moved(LumenRenderVertex vertex, float by) => vertex with { X = vertex.X + by };
        for (var index = start; index < quads.Count;)
        {
            var quad = quads[index];
            if (quad.Slice == 0 || index + 9 > quads.Count)
            {
                LumenRenderVertex at(LumenRenderVertex vertex) => vertex with { X = map(vertex.X) };
                quads[index++] = quad with { TopLeft = at(quad.TopLeft), TopRight = at(quad.TopRight), BottomRight = at(quad.BottomRight), BottomLeft = at(quad.BottomLeft) };
                continue;
            }
            // Nine of a sliced shape: the left column moves with the shape's left side, the right with its right side,
            // the middle spans between them.
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            for (var piece = index; piece < index + 9; piece++)
                foreach (var vertex in (ReadOnlySpan<LumenRenderVertex>)[quads[piece].TopLeft, quads[piece].TopRight, quads[piece].BottomRight, quads[piece].BottomLeft])
                    (left, right) = (MathF.Min(left, vertex.X), MathF.Max(right, vertex.X));
            var (byLeft, byRight) = (map(left) - left, map(right) - right);
            for (var piece = index; piece < index + 9; piece++)
            {
                var sliced = quads[piece];
                var centre = (sliced.TopLeft.X + sliced.TopRight.X + sliced.BottomRight.X + sliced.BottomLeft.X) / 4;
                float by(LumenRenderVertex vertex) => sliced.Slice switch { 1 => byLeft, 3 => byRight, _ => vertex.X <= centre ? byLeft : byRight };
                quads[piece] = sliced with
                {
                    TopLeft = moved(sliced.TopLeft, by(sliced.TopLeft)), TopRight = moved(sliced.TopRight, by(sliced.TopRight)),
                    BottomRight = moved(sliced.BottomRight, by(sliced.BottomRight)), BottomLeft = moved(sliced.BottomLeft, by(sliced.BottomLeft)),
                };
            }
            index += 9;
        }
    }

    private void appendInstanceDrawn(
        DisplayInstance instance,
        LumenMatrix parentTransform,
        ColorState parentColor,
        LumenRenderBlend parentBlend,
        float interpolationFraction,
        ImmutableArray<LumenRenderQuad>.Builder quads,
        byte maskDepth = 0)
    {
        if (instance.Removed || !instance.Visible || instance.HiddenByHost)
            return;
        // A newly revealed or abruptly moved parent must show its children at the same tick.
        // Otherwise hidden child positions are interpolated under the parent's new opacity.
        var childInterpolationFraction = interpolationFraction < 1 && isInterpolationCut(instance)
            ? 1 : interpolationFraction;
        var local = interpolate(instance, interpolationFraction);
        var transform = local.Transform.Then(parentTransform);
        // A clip anchored to a stage edge (a package's "anchor"): it, and what it holds, moves with that edge.
        if (anchorOf(instance) is { } anchor)
            transform = Rendering.LumenStage.ClipAnchored(transform, anchor, SceneAnchor, SceneScale);
        var color = local.Color.Then(parentColor);
        var blend = instance.BlendMode switch
        {
            3 => LumenRenderBlend.Multiply,
            4 => LumenRenderBlend.Screen,
            8 => LumenRenderBlend.Add,
            _ => parentBlend,
        };
        if (_texts.TryGetValue(instance.CharacterId, out var textField))
            appendText(instance, textField, transform, color, blend, maskDepth, quads);
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
                    var slot = instance.Name.Length > 0 ? instance.Name : instance.Parent?.Name ?? "";
                    if (scopedFill(instance, slot) is { } nativeFill || _nativeFills.TryGetValue(slot, out nativeFill))
                    {
                        float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
                        foreach (var vertex in geometry.Vertices)
                        {
                            (minX, maxX) = (MathF.Min(minX, vertex.X), MathF.Max(maxX, vertex.X));
                            (minY, maxY) = (MathF.Min(minY, vertex.Y), MathF.Max(maxY, vertex.Y));
                        }
                        if (maxX <= minX || maxY <= minY)
                        {
                            reportOnce("LUM_NATIVE_FILL_EMPTY", instance.CharacterId, instance.Frame, "Fill-zero/native shape geometry has empty bounds.");
                            continue;
                        }
                        var placed = nativeFill.Placement is { UniformScale: true } ? uniformScale(transform) : transform;
                        quads.Add(new LumenRenderQuad(
                            0,
                            transformNativeVertex(geometry.Vertices[0], placed, minX, maxX, minY, maxY, nativeFill.Placement),
                            transformNativeVertex(geometry.Vertices[1], placed, minX, maxX, minY, maxY, nativeFill.Placement),
                            transformNativeVertex(geometry.Vertices[2], placed, minX, maxX, minY, maxY, nativeFill.Placement),
                            transformNativeVertex(geometry.Vertices[3], placed, minX, maxX, minY, maxY, nativeFill.Placement),
                            color.Multiply,
                            color.Add,
                            blend,
                            NativeSurface: nativeFill.Surface, MaskDepth: maskDepth,
                            Source: Inspect ? describe(instance, $"native fill '{slot}'") : null));
                        continue;
                    }
                    reportOnce("LUM_NATIVE_FILL_DEFERRED", instance.CharacterId, instance.Frame, "Fill-zero/native shape '{0}' has no host surface yet.", slot);
                    continue;
                }

                if (ShapeSlices is { Count: > 0 } slices && slices.TryGetValue(instance.CharacterId, out var slice))
                {
                    appendSliced(geometry, slice, transform, color, blend, maskDepth, quads);
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
                    blend, MaskDepth: maskDepth,
                    Source: Inspect ? describe(instance, $"texture {geometry.TextureIndex}") : null));
            }
        }

        if (instance.LoadedMovie is { } loadedMovie)
        {
            // ponytail: the loaded movie's colour offsets ignore this clip's; multiply only.
            LumenRenderVertex place(LumenRenderVertex vertex)
            {
                var position = transform.Transform(vertex.X, vertex.Y);
                return vertex with { X = position.X, Y = position.Y };
            }
            var first = quads.Count;
            loadedMovie.Player.AppendRenderQuads(interpolationFraction, quads);
            for (var index = first; index < quads.Count; index++)
            {
                var quad = quads[index];
                quads[index] = quad with
                {
                    TextureIndex = quad.NativeSurface is null ? checked(loadedMovie.TextureOffset + quad.TextureIndex) : 0,
                    TopLeft = place(quad.TopLeft),
                    TopRight = place(quad.TopRight),
                    BottomRight = place(quad.BottomRight),
                    BottomLeft = place(quad.BottomLeft),
                    MultiplyColor = new LumenRenderColor(quad.MultiplyColor.Red * color.Multiply.Red,
                        quad.MultiplyColor.Green * color.Multiply.Green, quad.MultiplyColor.Blue * color.Multiply.Blue,
                        quad.MultiplyColor.Alpha * color.Multiply.Alpha),
                    MaskDepth = maskDepth,
                };
            }
        }

        if (_overlays.Count > 0 && instance.Name.Length > 0 && findOverlay(instance) is { } overlay)
        {
            var (x0, y0) = (overlay.Box.X, overlay.Box.Y);
            var (x1, y1) = (x0 + overlay.Box.Width, y0 + overlay.Box.Height);
            LumenRenderVertex corner(float x, float y, float u, float v)
            {
                var position = transform.Transform(x, y);
                return new LumenRenderVertex(position.X, position.Y, u, v);
            }
            quads.Add(new LumenRenderQuad(0, corner(x0, y0, 0, 0), corner(x1, y0, 1, 0), corner(x1, y1, 1, 1),
                corner(x0, y1, 0, 1), color.Multiply, color.Add, blend, NativeSurface: overlay.Surface, MaskDepth: maskDepth));
            if (overlay.Replace)
                return;
        }

        if (instance.Children.Count == 0) return;

        // Masks apply only to following siblings through the inclusive authored depth.
        // Scope each child separately so crossing depth ranges and nested sprites compose safely.
        var masks = _masks[_maskLevel];
        var firstMask = masks.Entries.Count;
        var firstMaskQuad = masks.Quads.Count;
        var children = instance.Children.Values;
        // An index loop: drawing changes no children, and it allocates no enumerator.
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            var child = children[childIndex];
            for (var index = masks.Entries.Count - 1; index >= firstMask; index--)
                if (masks.Entries[index].End < child.Depth)
                    masks.Entries.RemoveAt(index);
            if (child.ClipDepth is { } end && end > child.Depth)
            {
                var start = masks.Quads.Count;
                _maskLevel++; // masks inside the mask's own content go on the next level: this level's list is being written
                if (_maskLevel == _masks.Count)
                    _masks.Add(new MaskStack());
                appendInstance(child, transform, color, blend, childInterpolationFraction, masks.Quads);
                _maskLevel--;
                for (var index = start; index < masks.Quads.Count; index++)
                {
                    if (masks.Quads[index].MaskOperation == LumenRenderMaskOperation.Draw)
                        continue;
                    reportOnce("LUM_NESTED_MASK_SHAPE", child.CharacterId, child.Frame,
                        "A mask containing its own clipped content is not supported; its range is hidden.");
                    masks.Quads.Count = start;
                    break;
                }
                masks.Entries.Add((end, start, masks.Quads.Count - start));
                continue;
            }
            var active = masks.Entries.Count - firstMask;
            if (active == 0)
            {
                appendInstance(child, transform, color, blend, childInterpolationFraction, quads, maskDepth);
                continue;
            }
            if (maskDepth + active > byte.MaxValue)
            {
                reportOnce("LUM_MASK_DEPTH_LIMIT", child.CharacterId, child.Frame, "Mask nesting exceeds the stencil limit.");
                continue;
            }
            for (var index = 0; index < active; index++)
            {
                var (_, start, count) = masks.Entries[firstMask + index];
                for (var quad = start; quad < start + count; quad++)
                    quads.Add(masks.Quads[quad] with { MaskOperation = LumenRenderMaskOperation.Push, MaskDepth = (byte)(maskDepth + index) });
            }
            appendInstance(child, transform, color, blend, childInterpolationFraction, quads, (byte)(maskDepth + active));
            for (var index = active - 1; index >= 0; index--)
            {
                var (_, start, count) = masks.Entries[firstMask + index];
                for (var quad = start; quad < start + count; quad++)
                    quads.Add(masks.Quads[quad] with { MaskOperation = LumenRenderMaskOperation.Pop, MaskDepth = (byte)(maskDepth + index + 1) });
            }
        }
        masks.Entries.RemoveRange(firstMask, masks.Entries.Count - firstMask);
        masks.Quads.Count = firstMaskQuad;
    }

    // The masks in force while the display list is walked: a stack of their geometry kept between frames (an array per mask
    // per frame was most of Song Select's drawing garbage). One per nesting of masks inside a mask's own content.
    private sealed class MaskStack
    {
        public readonly List<(long End, int Start, int Count)> Entries = [];
        public readonly ImmutableArray<LumenRenderQuad>.Builder Quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
    }

    private readonly List<MaskStack> _masks = [new()];
    private int _maskLevel;

    // A walk that threw left its masks behind.
    private void resetMasks()
    {
        _maskLevel = 0;
        foreach (var level in _masks)
        {
            level.Entries.Clear();
            level.Quads.Clear();
        }
    }

    private readonly record struct NativeFillBinding(
        LumenNativeSurfaceKey Surface,
        LumenNativeSurfacePlacement? Placement);

    private void reportOnce(string code, uint characterId, int frame, string message) =>
        reportOnce(code, characterId, frame, message, null);

    // A message with one argument ({0}) is formatted only the first time: some are reported every frame (a fill the host
    // has not drawn yet).
    // format false: the message is final, argument only keys it.
    private void reportOnce(string code, uint characterId, int frame, string message, string? argument, bool format = true)
    {
        if (_diagnosticKeys.Add((code, characterId, frame, argument ?? message)))
        {
            _diagnostics.Add(new LumenRuntimeDiagnostic(
                LumenRuntimeDiagnosticSeverity.Warning,
                code,
                characterId,
                frame,
                argument is null || !format ? message : string.Format(System.Globalization.CultureInfo.InvariantCulture, message, argument)));
        }
    }

    // The tree walks run every tick over every clip: index loops (SortedList's enumerators are heap objects).
    private static void collectPlayingInstances(DisplayInstance instance, List<DisplayInstance> destination)
    {
        if (instance.Removed)
            return;
        if (instance.Frame >= 0)
            destination.Add(instance);
        var children = instance.Children.Values;
        for (var index = 0; index < children.Count; index++)
            collectPlayingInstances(children[index], destination);
    }

    private static void collectInstancesPostOrder(DisplayInstance instance, List<DisplayInstance> destination)
    {
        if (instance.Removed)
            return;
        var children = instance.Children.Values;
        for (var index = children.Count - 1; index >= 0; index--)
            collectInstancesPostOrder(children[index], destination);
        destination.Add(instance);
    }

    private static void snapshotInstances(DisplayInstance instance)
    {
        if (instance.Removed)
            return;
        instance.PreviousTransform = instance.Transform;
        instance.PreviousMultiply = instance.Color.Multiply;
        instance.PreviousVisible = instance.Visible;
        var children = instance.Children.Values;
        for (var index = 0; index < children.Count; index++)
            snapshotInstances(children[index]);
    }

    // A clip's children in depth order without SortedList's enumerator (a heap object per walk, per clip, per tick).
    private static Children childrenOf(DisplayInstance instance) => new(instance.Children);

    private readonly struct Children(SortedList<long, DisplayInstance> list)
    {
        public Enumerator GetEnumerator() => new(list);

        public struct Enumerator(SortedList<long, DisplayInstance> list)
        {
            private int _index = -1;

            public readonly DisplayInstance Current => list.GetValueAtIndex(_index);

            public bool MoveNext() => ++_index < list.Count;
        }
    }

    private static void resetInterpolation(DisplayInstance instance)
    {
        if (instance.Removed)
            return;
        instance.PreviousTransform = instance.Transform;
        instance.PreviousMultiply = instance.Color.Multiply;
        instance.PreviousVisible = instance.Visible;
        foreach (var child in childrenOf(instance))
            resetInterpolation(child);
    }

    private static void clearChildren(DisplayInstance instance)
    {
        foreach (var child in childrenOf(instance))
            child.Removed = true;
        instance.Children.Clear();
    }

    private static InterpolatedState interpolate(DisplayInstance instance, float fraction)
    {
        if (fraction >= 1 || isInterpolationCut(instance))
        {
            return new InterpolatedState(instance.Transform, instance.Color);
        }

        var previousTransform = instance.PreviousTransform!.Value;
        var previousMultiply = instance.PreviousMultiply!.Value;
        var currentTransform = instance.Transform;

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

    private static bool isInterpolationCut(DisplayInstance instance) =>
        instance.PreviousTransform is not LumenMatrix previousTransform
        || instance.PreviousMultiply is not LumenRenderColor previousMultiply
        || instance.PreviousVisible == false && instance.Visible
        || Math.Abs(instance.Transform.X - previousTransform.X)
            + Math.Abs(instance.Transform.Y - previousTransform.Y) > TranslationCutThreshold
        || colorDistanceExceeds(previousMultiply, instance.Color.Multiply, ColorCutThreshold);

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

    // A 9-sliced shape (a package's "slice": insets from each side, in its own units): nine quads, row by row, their
    // columns marked (LumenRenderQuad.Slice) so a stretch moves the corners and caps and widens only the middle.
    private static void appendSliced(LmbShapeGeometry geometry, (float Left, float Top, float Right, float Bottom) slice, LumenMatrix transform,
        ColorState color, LumenRenderBlend blend, byte maskDepth, ImmutableArray<LumenRenderQuad>.Builder quads)
    {
        var vertices = geometry.Vertices;
        float x0 = vertices.Min(static v => v.X), x1 = vertices.Max(static v => v.X), y0 = vertices.Min(static v => v.Y), y1 = vertices.Max(static v => v.Y);
        var topLeft = vertices.MinBy(static v => v.X + v.Y);
        var bottomRight = vertices.MaxBy(static v => v.X + v.Y);
        float width = x1 - x0, height = y1 - y0;
        if (width <= 0 || height <= 0)
            return;
        float[] xs = [x0, x0 + Math.Min(slice.Left, width / 2), x1 - Math.Min(slice.Right, width / 2), x1];
        float[] ys = [y0, y0 + Math.Min(slice.Top, height / 2), y1 - Math.Min(slice.Bottom, height / 2), y1];
        float u(float x) => topLeft.U + (bottomRight.U - topLeft.U) * (x - x0) / width;
        float v(float y) => topLeft.V + (bottomRight.V - topLeft.V) * (y - y0) / height;
        LumenRenderVertex at(float x, float y)
        {
            var (px, py) = transform.Transform(x, y);
            return new LumenRenderVertex(px, py, u(x), v(y));
        }
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                quads.Add(new LumenRenderQuad(geometry.TextureIndex,
                    at(xs[column], ys[row]), at(xs[column + 1], ys[row]), at(xs[column + 1], ys[row + 1]), at(xs[column], ys[row + 1]),
                    color.Multiply, color.Add, blend, MaskDepth: maskDepth) { Slice = (byte)(column + 1) });
    }

    private static LumenRenderVertex transformVertex(LmbVertex vertex, LumenMatrix transform)
    {
        var position = transform.Transform(vertex.X, vertex.Y);
        return new LumenRenderVertex(position.X, position.Y, vertex.U, vertex.V);
    }

    // A text field's own settings (a package's field: its style, its text, its bind), read once per field.
    private readonly record struct FieldText(int Settings, string? Text, string? Bind);
    private readonly Dictionary<LmbTextDefinition, FieldText> _fieldTexts = [];

    private FieldText fieldText(LmbTextDefinition field, string html)
    {
        if (_fieldTexts.TryGetValue(field, out var known))
            return known;
        var made = default(FieldText);
        if (LumenTextSurface.SettingsOf(html) is { } json)
            try
            {
                using var settings = System.Text.Json.JsonDocument.Parse(json);
                string? read(string key) => settings.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
                made = new FieldText(LumenTextSurface.SettingsId(json), read("text"), read("bind"));
                // A bound field reads the provider as a script would (the editor's live link mirrors what was read).
                if (made.Bind is { } bind)
                {
                    _providerAsked.Add(bind);
                    UsesProvider = true;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        return _fieldTexts[field] = made;
    }

    // A text field shows what a script put in its `text`; else what the engine gives the Wd name it is bound to; else
    // its package's own text (the authored placeholder of a game's movie is not drawn): one quad over its box whose
    // native surface key carries everything the host needs to rasterize it.
    private void appendText(DisplayInstance instance, LmbTextDefinition field, LumenMatrix transform, ColorState color,
        LumenRenderBlend blend, byte maskDepth, ImmutableArray<LumenRenderQuad>.Builder quads)
    {
        if (field.BoundsIndex >= _movie.Bounds.Length)
            return;
        var html = field.HtmlStringIndex < _movie.Strings.Length ? _movie.Strings[(int)field.HtmlStringIndex].Value : "";
        var own = fieldText(field, html);
        var text = instance.Variables.TryGetValue("text", out var value) ? toAvmString(value)
            : own.Bind is { } bind && Provided(bind) is { Kind: not LumenHostValueKind.Undefined } given ? toAvmString(fromHostValue(given))
            : own.Text;
        if (text is not { Length: > 0 })
            return;
        var box = _movie.Bounds[(int)field.BoundsIndex];
        var key = LumenTextSurface.Key(text, box.Right - box.Left, box.Bottom - box.Top, field.FontSize,
            LumenTextSurface.Align(html), LumenTextSurface.Colour(html), own.Settings);
        LumenRenderVertex corner(float x, float y, float u, float v)
        {
            var position = transform.Transform(x, y);
            return new LumenRenderVertex(position.X, position.Y, u, v);
        }
        // A styled field's borders and shadow reach out of its box.
        var margin = own.Settings != 0 ? LumenTextSurface.Margin?.Invoke(own.Settings) ?? 0 : 0;
        quads.Add(new LumenRenderQuad(0, corner(box.Left - margin, box.Top - margin, 0, 0), corner(box.Right + margin, box.Top - margin, 1, 0),
            corner(box.Right + margin, box.Bottom + margin, 1, 1), corner(box.Left - margin, box.Bottom + margin, 0, 1), color.Multiply, color.Add, blend,
            NativeSurface: key, MaskDepth: maskDepth));
    }

    private static LumenRenderVertex transformNativeVertex(
        LmbVertex vertex,
        LumenMatrix transform,
        float minX,
        float maxX,
        float minY,
        float maxY,
        LumenNativeSurfacePlacement? placement)
    {
        var u = (vertex.X - minX) / (maxX - minX);
        var v = (vertex.Y - minY) / (maxY - minY);
        var position = placement is { } nativePlacement
            ? transform.Transform(
                nativePlacement.X + u * nativePlacement.Width,
                nativePlacement.Y + v * nativePlacement.Height)
            : transform.Transform(vertex.X, vertex.Y);
        return new LumenRenderVertex(
            position.X,
            position.Y,
            u,
            v);
    }

    private static LumenRenderColor convertColor(LmbColorTransform color) =>
        new(color.Red / 256f, color.Green / 256f, color.Blue / 256f, color.Alpha / 256f);

    private sealed class DisplayInstance(uint characterId, int hierarchyDepth, DisplayInstance? parent)
    {
        public uint CharacterId { get; } = characterId;

        public int HierarchyDepth { get; } = hierarchyDepth;

        public DisplayInstance? Parent { get; } = parent;

        public long Depth { get; set; }

        public string Name { get; set; } = "";

        public bool ScriptColored { get; set; }

        public LumenLoadedMovie? LoadedMovie { get; set; }
        public long? ClipDepth { get; set; }
        public uint PlacementId { get; set; } = uint.MaxValue;

        public int FirstFrame { get; set; }

        public int Frame { get; set; } = -1;

        public bool Removed { get; set; }

        public bool FromTimeline { get; set; }

        public bool Playing { get; set; } = true;

        public bool Visible { get; set; } = true;
        public bool HiddenByHost { get; set; }

        public bool ScriptTransformed { get; set; }

        public ushort BlendMode { get; set; }

        public LumenMatrix Transform { get; set; } = LumenMatrix.Identity;

        public ColorState Color { get; set; } = ColorState.Identity;

        public LumenMatrix? PreviousTransform { get; set; }

        public LumenRenderColor? PreviousMultiply { get; set; }

        public bool? PreviousVisible { get; set; }

        // A sorted list, not a SortedDictionary: enumerating that allocated a stack and its node array per clip per
        // frame (~45 MiB/s uncapped); a clip has tens of children, so its array insertion costs nothing.
        public SortedList<long, DisplayInstance> Children { get; } = [];

        public Dictionary<long, DisplayInstance>? ReusableTimelineChildren { get; set; }

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
        long Sequence,
        bool IsInitialization);

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

public sealed record LumenCallbackDescriptor(
    string Name,
    ImmutableArray<string> Parameters);

/// <summary>A movie loaded into a clip by MovieClipLoader, with its textures at TextureOffset.</summary>
public sealed record LumenLoadedMovie(LumenPlayer Player, uint TextureOffset);
