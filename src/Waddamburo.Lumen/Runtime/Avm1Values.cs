using Waddamburo.Formats.Lmb;

namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1Undefined
{
    public static Avm1Undefined Instance { get; } = new();

    private Avm1Undefined()
    {
    }
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
    public IReadOnlyList<object?> Values { get; } = values;

    public Avm1Lookup GetIndexedProperty(string name)
    {
        if (name == "length")
            return new Avm1Lookup(true, (double)Values.Count);
        return int.TryParse(name, out var index) && (uint)index < (uint)Values.Count
            ? new Avm1Lookup(true, Values[index])
            : default;
    }
}

internal sealed class Avm1FunctionValue : Avm1Object
{
    public Avm1FunctionValue(Avm1FunctionOperand definition, Avm1CodeBlock body)
    {
        Definition = definition;
        Body = body;
        var prototype = new Avm1Object();
        prototype.Properties["constructor"] = this;
        Properties["prototype"] = prototype;
    }

    public Avm1FunctionOperand Definition { get; }

    public Avm1CodeBlock Body { get; }
}

internal sealed class Avm1NativeFunction(string name, LumenHostCallback callback) : Avm1Object
{
    public string Name { get; } = name;

    public LumenHostCallback Callback { get; } = callback;
}
