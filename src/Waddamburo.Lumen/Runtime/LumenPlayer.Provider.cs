using Waddamburo.Lumen.Provider;

namespace Waddamburo.Lumen.Runtime;

// The provider as a movie sees it (free-movies.md): a global Wd with
//   Wd.get(name)           the value now, undefined while the name has none
//   Wd.has(name)           whether it has one
//   Wd.on(name, fn)        a state: fn(value) at once (undefined while it has none) and at every change;
//                          an event: fn(arguments...) each time it happens
//   a list (WdKind.List)   Wd.get(name, index), Wd.has(name, index); Wd.on(name, fn): fn(index, value) at once for
//                          every place that has a value, then whenever one changes (undefined: it is gone). A
//                          list's length is a state of its own (dan.exam.count), so a movie holds only what it shows.
//   ...(name, ..., player) the same for one drum's player by number, 1 the left drum, 2 the right
// Without a player a movie gets its own drum's: a part made once serves either drum and never learns its number. A
// movie that belongs to no drum (one both share, like the song's title) gets player 1's.
// The engine side is Publish (state) and Emit (event). Scopes are a check, not a wall: any name may be asked for at
// any time, and one without a value reads undefined.
public sealed partial class LumenPlayer
{
    // Drum 0 is "this movie's own"; a state's index is -1, a list's places their own (no key text made per read).
    private readonly Dictionary<(string Name, int Index, int Drum), LumenHostValue> _provided = [];
    private readonly Dictionary<(string Name, int Drum), List<(Avm1FunctionValue Function, Avm1Value This)>> _providerListeners = [];
    private HashSet<string>? _providerWarned;
    private readonly HashSet<string> _providerAsked = new(StringComparer.Ordinal);
    private bool _hasOwnDrum;

    /// <summary>
    /// Takes the movie's <c>Wd.request(name, ...)</c>: null when the engine took it, else why not (the screen that set it
    /// decides; WdRequests). None set: no screen takes requests from this movie.
    /// </summary>
    public Func<string, LumenHostValue[], string?>? RequestHandler { get; set; }

    /// <summary>The movie's scripts name the provider (or it has asked it for something): it is one of ours, made for it.</summary>
    public bool UsesProvider { get; private set; }

    /// <summary>The provider's names this movie has read or listens to, so far.</summary>
    public IReadOnlyCollection<string> ProviderNames => _providerAsked;

    /// <summary>What this movie asked the provider for that is not one of its names (a mistyped name, most likely).</summary>
    public IReadOnlyCollection<string> ProviderUnknownNames => _providerWarned is { } warned ? warned : [];

    /// <summary>
    /// Told of everything the provider gives this movie, as it is given (a changed state, a list's place, an event): the
    /// skin editor's live link mirrors a running game's movies with it.
    /// </summary>
    public Action<LumenProvided>? ProviderTap { get; set; }

    /// <summary>Every state and list place this movie holds now (for a link joining late).</summary>
    public IEnumerable<LumenProvided> ProvidedNow() => _provided.Select(static pair => pair.Key.Index >= 0
        ? new LumenProvided(WdKind.List, pair.Key.Name, pair.Key.Drum, pair.Key.Index, [pair.Value])
        : new LumenProvided(WdKind.State, pair.Key.Name, pair.Key.Drum, 0, [pair.Value]));

    /// <summary>A state's value as this movie would read it; undefined while it has none.</summary>
    public LumenHostValue Provided(string name, int drum = 0) => _provided.GetValueOrDefault(own(name, -1, drum));

    /// <summary>One place of a list as this movie would read it; undefined while it has none.</summary>
    public LumenHostValue ProvidedItem(string name, int index, int drum = 0) => _provided.GetValueOrDefault(own(name, index, drum));

    // A movie of no drum reads player 1's where it asks for "its own"; a name of the whole app has no drum.
    private (string, int, int) own(string name, int index, int drum) =>
        (name, index, drum == 0 && !_hasOwnDrum && WdNames.Find(name)?.Scope != "app" ? 1 : drum);

    /// <summary>
    /// Sets a state's value for this movie; its listeners hear of a change. Undefined withdraws it.
    /// <paramref name="drum"/>: 0 for the drum this movie belongs to, or the player's number (1 left, 2 right).
    /// </summary>
    public void Publish(string name, LumenHostValue value, int drum = 0)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.State })
            throw new ArgumentException($"'{name}' is not a state of the provider (WdNames).", nameof(name));
        // A name of the whole app (credits, the screen) says nothing about which drum the movie belongs to.
        if (drum == 0 && WdNames.Find(name)!.Scope != "app") _hasOwnDrum = true;
        if (_provided.TryGetValue((name, -1, drum), out var before) ? before == value : value.Kind == LumenHostValueKind.Undefined)
            return;
        if (value.Kind == LumenHostValueKind.Undefined) _provided.Remove((name, -1, drum));
        else _provided[(name, -1, drum)] = value;
        ProviderTap?.Invoke(new(WdKind.State, name, drum, 0, [value]));
        tell(name, drum, [fromHostValue(value)]);
    }

    /// <summary>Sets one place of a list (WdKind.List) for this movie; undefined withdraws it. Listeners get the index and the value.</summary>
    public void PublishItem(string name, int index, LumenHostValue value, int drum = 0)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.List })
            throw new ArgumentException($"'{name}' is not a list of the provider (WdNames).", nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        // A name of the whole app (credits, the screen) says nothing about which drum the movie belongs to.
        if (drum == 0 && WdNames.Find(name)!.Scope != "app") _hasOwnDrum = true;
        var key = (name, index, drum);
        if (_provided.TryGetValue(key, out var before) ? before == value : value.Kind == LumenHostValueKind.Undefined)
            return;
        if (value.Kind == LumenHostValueKind.Undefined) _provided.Remove(key);
        else _provided[key] = value;
        ProviderTap?.Invoke(new(WdKind.List, name, drum, index, [value]));
        tell(name, drum, [(double)index, fromHostValue(value)]);
    }

    /// <summary>Something happened at this moment on this movie's drum; listeners get the arguments in WdNames' order.</summary>
    public void Emit(string name, params ReadOnlySpan<LumenHostValue> arguments) => EmitFor(0, name, arguments);

    /// <summary>As <see cref="Emit"/>, for one player's drum by number (1 left, 2 right).</summary>
    public void EmitFor(int drum, string name, params ReadOnlySpan<LumenHostValue> arguments)
    {
        if (WdNames.Find(name) is not { Kind: WdKind.Event })
            throw new ArgumentException($"'{name}' is not an event of the provider (WdNames).", nameof(name));
        ProviderTap?.Invoke(new(WdKind.Event, name, drum, 0, arguments.ToArray()));
        if (_providerListeners.Count == 0)
            return;
        var converted = new Avm1Value[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) converted[i] = fromHostValue(arguments[i]);
        tell(name, drum, converted);
    }

    private void tell(string name, int drum, Avm1Value[] arguments)
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
        string? known(IReadOnlyList<Avm1Value> arguments, string method)
        {
            UsesProvider = true;
            if (arguments.Count > 0 && arguments[0].String is { } name && WdNames.Find(name) is not null)
            {
                _providerAsked.Add(name);
                return name;
            }
            // A mistyped name is the quiet failure of a pull API: say so, once, naming the movie.
            var asked = arguments.Count > 0 ? arguments[0].ToString() : "(nothing)";
            if ((_providerWarned ??= []).Add(asked))
                Console.Error.WriteLine($"{Name ?? "A movie"}: Wd.{method}(\"{asked}\") is not a name the engine provides (version {WdNames.Version}).");
            return null;
        }
        wd.Properties["version"] = (double)WdNames.Version;
        // The player argument, where given: 1 or 2. Anything else is the movie's own drum.
        static int drum(IReadOnlyList<Avm1Value> arguments, int at) =>
            arguments.Count > at && arguments[at].TryGetNumber(out var number) && number is 1 or 2 ? (int)number : 0;
        // A list's name takes its index before the player: the key asked for, and where the player argument is.
        (string Name, int Index, int Drum) asked(string name, IReadOnlyList<Avm1Value> arguments)
        {
            if (WdNames.Find(name)!.Kind != WdKind.List)
                return own(name, -1, drum(arguments, 1));
            // A list read without a usable index finds nothing (-2 is no place).
            var index = arguments.Count > 1 && arguments[1].TryGetNumber(out var number) && number >= 0 ? (int)number : -2;
            return own(name, index, drum(arguments, 2));
        }
        wd.Properties["get"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "get") is { } name && _provided.TryGetValue(asked(name, arguments), out var value)
                ? fromHostValue(value) : Avm1Value.Undefined));
        wd.Properties["has"] = new Avm1MethodWrapper((_, arguments) => new Avm1Lookup(true,
            known(arguments, "has") is { } name && _provided.ContainsKey(asked(name, arguments))));
        wd.Properties["on"] = new Avm1MethodWrapper((self, arguments) =>
        {
            if (known(arguments, "on") is { } name && arguments.Count > 1 && arguments[1].Object is Avm1FunctionValue function)
            {
                var player = drum(arguments, 2);
                if (!_providerListeners.TryGetValue((name, player), out var listeners))
                    _providerListeners[(name, player)] = listeners = [];
                listeners.Add((function, self));
                // A state's listener is told where things stand at once, value or none (undefined): a movie draws its
                // "nothing yet" look from the same function as everything else. An event has no "now".
                if (WdNames.Find(name)!.Kind == WdKind.State)
                    invokeFunction(_root, function, self,
                        [_provided.TryGetValue(own(name, -1, player), out var value) ? fromHostValue(value) : Avm1Value.Undefined], _activeContext);
                // A list's listener: every place that has a value, in order.
                if (WdNames.Find(name)!.Kind == WdKind.List)
                {
                    var (_, _, from) = own(name, 0, player);
                    foreach (var (index, value) in _provided.Where(pair => pair.Key.Drum == from && pair.Key.Index >= 0 && pair.Key.Name == name)
                        .Select(pair => (pair.Key.Index, pair.Value)).OrderBy(static each => each.Index).ToArray())
                        invokeFunction(_root, function, self, [(double)index, fromHostValue(value)], _activeContext);
                }
            }
            return new Avm1Lookup(true, Avm1Value.Undefined);
        });
        // Wd.request(name, ...): true when the engine took it. A refusal is no error in the movie; why is printed.
        wd.Properties["request"] = new Avm1MethodWrapper((_, arguments) =>
        {
            if (known(arguments, "request") is not { } name)
                return new Avm1Lookup(true, false);
            var values = arguments.Skip(1).Select(toHostValue).ToArray();
            var refused = WdNames.Find(name)!.Kind != WdKind.Request ? $"{name} is not a request (Wd.get or Wd.on reads it)"
                : WdNames.Find(name)!.Scope == "app" ? WdRequests.App?.Take(name, values) ?? "the engine takes no app requests here"
                : RequestHandler is null ? "no screen takes requests from this movie"
                : RequestHandler(name, values);
            var call = $"Wd.request(\"{name}\"{string.Concat(values.Select(static value => ", " + describe(value)))})";
            if (LumenEdgeLog.Enabled)
                edge("out", $"{call} -> {refused ?? "taken"}");
            if (refused is not null)
                Console.Error.WriteLine($"{Name ?? "A movie"}: {call} refused: {refused}.");
            return new Avm1Lookup(true, refused is null);
        });
        return wd;
    }
}

/// <summary>Something the provider gave a movie: a state (one value), a list's place, or an event (its arguments).</summary>
public readonly record struct LumenProvided(WdKind Kind, string Name, int Drum, int Index, LumenHostValue[] Values);
