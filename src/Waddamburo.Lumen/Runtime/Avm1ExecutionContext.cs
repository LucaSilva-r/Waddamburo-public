namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1ExecutionContext(
    Func<string, Avm1Lookup> variableReader,
    Action<string, object?> variableWriter,
    Func<object?, string, Avm1Lookup> memberReader,
    Action<object?, string, object?> memberWriter,
    Func<string, IReadOnlyList<object?>, Avm1Lookup> functionCaller)
{
    private readonly Dictionary<string, object?> _variableWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<MemberKey, object?> _memberWrites = [];

    public Avm1Lookup GetVariable(string name) =>
        _variableWrites.TryGetValue(name, out var value)
            ? new Avm1Lookup(true, value)
            : variableReader(name);

    public void SetVariable(string name, object? value) => _variableWrites[name] = value;

    public Avm1Lookup GetMember(object? target, string name) =>
        _memberWrites.TryGetValue(new MemberKey(target, name), out var value)
            ? new Avm1Lookup(true, value)
            : memberReader(target, name);

    public void SetMember(object? target, string name, object? value) =>
        _memberWrites[new MemberKey(target, name)] = value;

    public Avm1Lookup CallFunction(string name, IReadOnlyList<object?> arguments) =>
        functionCaller(name, arguments);

    public void Commit()
    {
        foreach (var (name, value) in _variableWrites)
            variableWriter(name, value);
        foreach (var (key, value) in _memberWrites)
            memberWriter(key.Target, key.Name, value);
    }

    private readonly record struct MemberKey(object? Target, string Name);
}

internal readonly record struct Avm1Lookup(bool Found, object? Value);
