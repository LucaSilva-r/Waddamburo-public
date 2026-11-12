using Waddamburo.Lumen.Provider;

namespace Waddamburo.Lumen.Runtime;

// The provider as a movie sees it (movie-provider-design.md): a global Wd with
//   Wd.get(name)      the value now, undefined while the name has none
//   Wd.has(name)      whether it has one
//   Wd.on(name, fn)   a state: fn(value) at once (undefined while it has none) and at every change;
//                     an event: fn(arguments...) each time it happens
// The engine side is Publish (state) and Emit (event). Scopes are a check, not a wall: any name may be asked for at
// any time, and one without a value reads undefined.
public sealed partial class LumenPlayer
{
    private readonly Dictionary<string, LumenHostValue> _provided = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(Avm1FunctionValue Function, object? This)>> _providerListeners = new(StringComparer.Ordinal);
    private HashSet<string>? _providerWarned;
    private readonly HashSet<string> _providerAsked = new(StringComparer.Ordinal);

    /// <summary>The provider's names this movie has read or listens to, so far.</summary>
    public IReadOnlyCollection<string> ProviderNames => _providerAsked;

    /// <summary>What this movie asked the provider for that is not one of its names (a mistyped name, most likely).</summary>
    public IReadOnlyCollection<string> ProviderUnknownNames => _providerWarned is { } warned ? warned : [];

    /// <summary>A state's value as this movie would read it; undefined while it has none.</summary>
    public LumenHostValue Provided(string name) => _provided.GetValueOrDefault(name);

    /// <summary>The movie has asked the provider for something: it is one of ours, made for it.</summary>
    public bool UsesProvider { get; private set; }

    /// <summary>Sets a state's value for this movie; its listeners hear of a change. Undefined withdraws it.</summary>
    public void Publish(string name, LumenHostValue value)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.State })
            throw new ArgumentException($"'{name}' is not a state of the provider (WdNames).", nameof(name));
        if (_provided.TryGetValue(name, out var before) ? before == value : value.Kind == LumenHostValueKind.Undefined)
            return;
        if (value.Kind == LumenHostValueKind.Undefined) _provided.Remove(name);
        else _provided[name] = value;
        tell(name, [fromHostValue(value)]);
    }

    /// <summary>Something happened at this moment; listeners get the arguments in the order WdNames gives.</summary>
    public void Emit(string name, params ReadOnlySpan<LumenHostValue> arguments)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.Event })
            throw new ArgumentException($"'{name}' is not an event of the provider (WdNames).", nameof(name));
        if (!_providerListeners.ContainsKey(name))
            return;
        var converted = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) converted[i] = fromHostValue(arguments[i]);
        tell(name, converted);
    }

    private void tell(string name, object?[] arguments)
    {
        if (!_providerListeners.TryGetValue(name, out var listeners))
            return;
        if (LumenEdgeLog.Enabled)
            edge("in", $"Wd {name}({string.Join(", ", arguments.Select(static value => describe(toHostValue(value))))})");
        foreach (var (function, self) in listeners.ToArray()) // a listener may add another
            invokeFunction(_root, function, self, arguments, _activeContext);
        drainActions();
    }

    private Avm1Object createProvider()
    {
        var wd = new Avm1Object();
        string? known(IReadOnlyList<object?> arguments, string method)
        {
            UsesProvider = true;
            if (arguments.Count > 0 && arguments[0] is string name && WdNames.Find(name) is not null)
            {
                _providerAsked.Add(name);
                return name;
            }
            // A mistyped name is the quiet failure of a pull API: say so, once, naming the movie.
            var asked = arguments.Count > 0 ? arguments[0]?.ToString() ?? "null" : "(nothing)";
            if ((_providerWarned ??= []).Add(asked))
                Console.Error.WriteLine($"{Name ?? "A movie"}: Wd.{method}(\"{asked}\") is not a name the engine provides (version {WdNames.Version}).");
            return null;
        }
        wd.Properties["version"] = (double)WdNames.Version;
        wd.Properties["get"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "get") is { } name && _provided.TryGetValue(name, out var value) ? fromHostValue(value) : Avm1Undefined.Instance));
        wd.Properties["has"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "has") is { } name && _provided.ContainsKey(name)));
        wd.Properties["on"] = new Avm1MethodWrapper((self, arguments) =>
        {
            if (known(arguments, "on") is { } name && arguments.Count > 1 && arguments[1] is Avm1FunctionValue function)
            {
                if (!_providerListeners.TryGetValue(name, out var listeners))
                    _providerListeners[name] = listeners = [];
                listeners.Add((function, self));
                // A state's listener is told where things stand at once, value or none (undefined): a movie draws its
                // "nothing yet" look from the same function as everything else. An event has no "now".
                if (WdNames.Find(name)!.Kind == WdKind.State)
                    invokeFunction(_root, function, self,
                        [_provided.TryGetValue(name, out var value) ? fromHostValue(value) : Avm1Undefined.Instance], _activeContext);
            }
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        });
        return wd;
    }
}
