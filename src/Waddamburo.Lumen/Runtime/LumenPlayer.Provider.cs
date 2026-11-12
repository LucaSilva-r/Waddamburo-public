using Waddamburo.Lumen.Provider;

namespace Waddamburo.Lumen.Runtime;

// The provider as a movie sees it (movie-provider-design.md): a global Wd with
//   Wd.get(name)           the value now, undefined while the name has none
//   Wd.has(name)           whether it has one
//   Wd.on(name, fn)        a state: fn(value) at once (undefined while it has none) and at every change;
//                          an event: fn(arguments...) each time it happens
//   ...(name, ..., player) the same for one drum's player by number, 1 the left drum, 2 the right
// Without a player a movie gets its own drum's: a part made once serves either drum and never learns its number. A
// movie that belongs to no drum (one both share, like the song's title) gets player 1's.
// The engine side is Publish (state) and Emit (event). Scopes are a check, not a wall: any name may be asked for at
// any time, and one without a value reads undefined.
public sealed partial class LumenPlayer
{
    // Drum 0 is "this movie's own".
    private readonly Dictionary<(string Name, int Drum), LumenHostValue> _provided = [];
    private readonly Dictionary<(string Name, int Drum), List<(Avm1FunctionValue Function, object? This)>> _providerListeners = [];
    private HashSet<string>? _providerWarned;
    private readonly HashSet<string> _providerAsked = new(StringComparer.Ordinal);
    private bool _hasOwnDrum;

    /// <summary>The movie has asked the provider for something: it is one of ours, made for it.</summary>
    public bool UsesProvider { get; private set; }

    /// <summary>The provider's names this movie has read or listens to, so far.</summary>
    public IReadOnlyCollection<string> ProviderNames => _providerAsked;

    /// <summary>What this movie asked the provider for that is not one of its names (a mistyped name, most likely).</summary>
    public IReadOnlyCollection<string> ProviderUnknownNames => _providerWarned is { } warned ? warned : [];

    /// <summary>A state's value as this movie would read it; undefined while it has none.</summary>
    public LumenHostValue Provided(string name, int drum = 0) => _provided.GetValueOrDefault(own(name, drum));

    // A movie of no drum reads player 1's where it asks for "its own".
    private (string, int) own(string name, int drum) => (name, drum == 0 && !_hasOwnDrum ? 1 : drum);

    /// <summary>
    /// Sets a state's value for this movie; its listeners hear of a change. Undefined withdraws it.
    /// <paramref name="drum"/>: 0 for the drum this movie belongs to, or the player's number (1 left, 2 right).
    /// </summary>
    public void Publish(string name, LumenHostValue value, int drum = 0)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.State })
            throw new ArgumentException($"'{name}' is not a state of the provider (WdNames).", nameof(name));
        if (drum == 0) _hasOwnDrum = true;
        if (_provided.TryGetValue((name, drum), out var before) ? before == value : value.Kind == LumenHostValueKind.Undefined)
            return;
        if (value.Kind == LumenHostValueKind.Undefined) _provided.Remove((name, drum));
        else _provided[(name, drum)] = value;
        tell(name, drum, [fromHostValue(value)]);
    }

    /// <summary>Something happened at this moment on this movie's drum; listeners get the arguments in WdNames' order.</summary>
    public void Emit(string name, params ReadOnlySpan<LumenHostValue> arguments) => EmitFor(0, name, arguments);

    /// <summary>As <see cref="Emit"/>, for one player's drum by number (1 left, 2 right).</summary>
    public void EmitFor(int drum, string name, params ReadOnlySpan<LumenHostValue> arguments)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.Event })
            throw new ArgumentException($"'{name}' is not an event of the provider (WdNames).", nameof(name));
        if (_providerListeners.Count == 0)
            return;
        var converted = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) converted[i] = fromHostValue(arguments[i]);
        tell(name, drum, converted);
    }

    private void tell(string name, int drum, object?[] arguments)
    {
        var told = false;
        void to(int listening)
        {
            if (!_providerListeners.TryGetValue((name, listening), out var listeners))
                return;
            if (LumenEdgeLog.Enabled)
                edge("in", $"Wd {name}{(listening == 0 ? "" : $" [player {listening}]")}({string.Join(", ", arguments.Select(static value => describe(toHostValue(value))))})");
            foreach (var (function, self) in listeners.ToArray()) // a listener may add another
                invokeFunction(_root, function, self, arguments, _activeContext);
            told = true;
        }
        to(drum);
        if (drum == 1 && !_hasOwnDrum) to(0); // a movie of no drum hears player 1 as "its own"
        if (told) drainActions();
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
        // The player argument, where given: 1 or 2. Anything else is the movie's own drum.
        static int drum(IReadOnlyList<object?> arguments, int at) =>
            arguments.Count > at && arguments[at] is double number && number is 1 or 2 ? (int)number : 0;
        wd.Properties["get"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "get") is { } name && _provided.TryGetValue(own(name, drum(arguments, 1)), out var value)
                ? fromHostValue(value) : Avm1Undefined.Instance));
        wd.Properties["has"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "has") is { } name && _provided.ContainsKey(own(name, drum(arguments, 1)))));
        wd.Properties["on"] = new Avm1MethodWrapper((self, arguments) =>
        {
            if (known(arguments, "on") is { } name && arguments.Count > 1 && arguments[1] is Avm1FunctionValue function)
            {
                var player = drum(arguments, 2);
                if (!_providerListeners.TryGetValue((name, player), out var listeners))
                    _providerListeners[(name, player)] = listeners = [];
                listeners.Add((function, self));
                // A state's listener is told where things stand at once, value or none (undefined): a movie draws its
                // "nothing yet" look from the same function as everything else. An event has no "now".
                if (WdNames.Find(name)!.Kind == WdKind.State)
                    invokeFunction(_root, function, self,
                        [_provided.TryGetValue(own(name, player), out var value) ? fromHostValue(value) : Avm1Undefined.Instance], _activeContext);
            }
            return new Avm1Lookup(true, Avm1Undefined.Instance);
        });
        return wd;
    }
}
