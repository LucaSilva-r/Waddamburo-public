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
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public Avm1LexicalScope? Parent { get; } = parent;

    public bool ContainsOwn(string name) => _values.ContainsKey(name);

    public bool TryGetOwn(string name, out object? value) => _values.TryGetValue(name, out value);

    public bool TryGet(string name, out object? value)
    {
        if (_values.TryGetValue(name, out value))
            return true;
        if (Parent is not null)
            return Parent.TryGet(name, out value);
        value = null;
        return false;
    }

    public void Define(string name, object? value) => _values[name] = value;
}

internal class Avm1Object
{
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

    public Avm1Object? Prototype { get; set; }

    public Avm1Lookup GetProperty(string name)
    {
        if (Properties.TryGetValue(name, out var value))
            return new Avm1Lookup(true, value);
        return Prototype?.GetProperty(name) ?? default;
    }
}

internal sealed class Avm1ArrayObject(IReadOnlyList<object?> values) : Avm1Object
{
    private readonly List<object?> _values = [.. values];

    public IReadOnlyList<object?> Values => _values;

    public Avm1Lookup GetIndexedProperty(string name)
    {
        if (name == "length")
            return new Avm1Lookup(true, (double)_values.Count);
        return int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && (uint)index < (uint)_values.Count
            ? new Avm1Lookup(true, _values[index])
            : default;
    }

    public bool TrySetIndexedProperty(string name, object? value, int maximumLength)
    {
        if (name == "length")
        {
            var requested = value switch
            {
                double number when double.IsFinite(number) && number == Math.Truncate(number) => number,
                _ => -1,
            };
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
                _values.Add(Avm1Undefined.Instance);
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
        prototype.Properties["constructor"] = this;
        Properties["prototype"] = prototype;
    }

    public Avm1FunctionOperand Definition { get; }

    public Avm1CodeBlock Body { get; }

    public bool IsFunction2 { get; }

    public Avm1Object? OwnerPrototype { get; set; }

    public Avm1LexicalScope? CapturedScope { get; set; }

    public object? DefinitionTarget { get; set; }
}

internal sealed class Avm1NativeFunction(string name, LumenHostCallback callback) : Avm1Object
{
    public string Name { get; } = name;

    public LumenHostCallback Callback { get; } = callback;
}

internal sealed record Avm1SuperValue(object? ThisValue, Avm1Object? Level);
