using Waddamburo.Formats.Lmb;

namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1Undefined
{
    public static Avm1Undefined Instance { get; } = new();

    private Avm1Undefined()
    {
    }
}

internal sealed class Avm1LexicalScope(Avm1LexicalScope? parent = null)
{
    private static int s_nextId;
    // A function call's locals: a handful, so a flat list searched in order beats a dictionary (made per call and grown
    // by resizing as its locals were defined, a large share of a script's garbage). Names are mostly the movie's own
    // interned strings, so the reference check usually settles a comparison.
    private string[] _names = s_empty;
    private Avm1Value[] _values = s_noValues;
    private int _count;
    private static readonly string[] s_empty = [];
    private static readonly Avm1Value[] s_noValues = [];

    public Avm1LexicalScope? Parent { get; private set; } = parent;

    /// <summary>A function defined in this scope keeps it (a closure): it must outlive its call.</summary>
    public bool Captured { get; set; }

    // A call's scope that no closure kept goes back here when its call has committed.
    [ThreadStatic] private static Stack<Avm1LexicalScope>? s_free;

    public static Avm1LexicalScope Rent(Avm1LexicalScope? parent)
    {
        if ((s_free ??= new()).TryPop(out var scope))
        {
            scope.Parent = parent;
            return scope;
        }
        return new Avm1LexicalScope(parent);
    }

    public static void Return(Avm1LexicalScope scope)
    {
        if (scope.Captured || s_free!.Count >= 256)
            return;
        Array.Clear(scope._values, 0, scope._count); // keep nothing alive from the finished call
        Array.Clear(scope._names, 0, scope._count);
        scope._count = 0;
        scope.Parent = null;
        s_free.Push(scope);
    }

    /// <summary>A cheap hash for the pending-write keys: a scope is made per function call and hashed at once, and an
    /// object's first identity hash takes the runtime's slow path.</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextId);

    public bool ContainsOwn(string name) => indexOf(name) >= 0;

    public bool TryGetOwn(string name, out Avm1Value value)
    {
        var at = indexOf(name);
        value = at >= 0 ? _values[at] : default;
        return at >= 0;
    }

    public bool TryGet(string name, out Avm1Value value)
    {
        if (TryGetOwn(name, out value))
            return true;
        if (Parent is not null)
            return Parent.TryGet(name, out value);
        value = default;
        return false;
    }

    public void Define(string name, Avm1Value value)
    {
        var at = indexOf(name);
        if (at >= 0)
        {
            _values[at] = value;
            return;
        }
        if (_count == _names.Length)
        {
            var size = Math.Max(8, _count * 2);
            Array.Resize(ref _names, size);
            Array.Resize(ref _values, size);
        }
        _names[_count] = name;
        _values[_count++] = value;
    }

    private int indexOf(string name)
    {
        for (var index = 0; index < _count; index++)
            if (ReferenceEquals(_names[index], name))
                return index;
        for (var index = 0; index < _count; index++)
            if (string.Equals(_names[index], name, StringComparison.Ordinal))
                return index;
        return -1;
    }
}

internal class Avm1Object(int capacity = 0)
{
    public Dictionary<string, Avm1Value> Properties { get; } = new(capacity, StringComparer.Ordinal);

    public Avm1Object? Prototype { get; set; }

    public Avm1Lookup GetProperty(string name)
    {
        if (Properties.TryGetValue(name, out var value))
            return new Avm1Lookup(true, value);
        return Prototype?.GetProperty(name) ?? default;
    }
}

internal sealed class Avm1ArrayObject(IReadOnlyList<Avm1Value> values) : Avm1Object
{
    private readonly List<Avm1Value> _values = [.. values];

    public IReadOnlyList<Avm1Value> Values => _values;

    public Avm1Lookup GetIndexedProperty(string name)
    {
        if (name == "length")
            return new Avm1Lookup(true, (double)_values.Count);
        return int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && (uint)index < (uint)_values.Count
            ? new Avm1Lookup(true, _values[index])
            : default;
    }

    public bool TrySetIndexedProperty(string name, Avm1Value value, int maximumLength)
    {
        if (name == "length")
        {
            var requested = value.TryGetNumber(out var number) && double.IsFinite(number) && number == Math.Truncate(number)
                ? number : -1;
            if (requested < 0 || requested > maximumLength)
                return false;
            resize(checked((int)requested));
            return true;
        }
        if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || index < 0)
        {
            return false;
        }
        if (index >= maximumLength)
            return false;
        if (index >= _values.Count)
            resize(index + 1);
        _values[index] = value;
        return true;
    }

    private void resize(int length)
    {
        if (length < _values.Count)
            _values.RemoveRange(length, _values.Count - length);
        else
            while (_values.Count < length)
                _values.Add(Avm1Value.Undefined);
    }
}

internal sealed class Avm1FunctionValue : Avm1Object
{
    public Avm1FunctionValue(Avm1FunctionOperand definition, Avm1CodeBlock body, bool isFunction2)
    {
        Definition = definition;
        Body = body;
        IsFunction2 = isFunction2;
        var prototype = new Avm1Object();
        prototype.Properties["constructor"] = Avm1Value.Of(this);
        Properties["prototype"] = prototype;
    }

    public Avm1FunctionOperand Definition { get; }

    public Avm1CodeBlock Body { get; }

    public bool IsFunction2 { get; }

    public Avm1Object? OwnerPrototype { get; set; }

    public Avm1LexicalScope? CapturedScope { get; set; }

    public object? DefinitionTarget { get; set; }
}

internal sealed class Avm1NativeFunction(string name, LumenHostCallback callback, bool host = false) : Avm1Object
{
    /// <summary>Registered by the game's host binding (not a built-in of the Flash library).</summary>
    public bool Host { get; } = host;

    public string Name { get; } = name;

    public LumenHostCallback Callback { get; } = callback;
}

/// <summary>A host wrapper standing in for a script method (see LumenPlayer.TryWrapScriptMethod); gets the real this.</summary>
internal sealed class Avm1MethodWrapper(Func<Avm1Value, IReadOnlyList<Avm1Value>, Avm1Lookup> invoke) : Avm1Object
{
    public Avm1Lookup Invoke(Avm1Value thisValue, IReadOnlyList<Avm1Value> arguments) => invoke(thisValue, arguments);
}

internal sealed record Avm1SuperValue(Avm1Value ThisValue, Avm1Object? Level);
