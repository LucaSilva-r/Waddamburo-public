namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1ExecutionContext(
    Func<string, Avm1Lookup> variableReader,
    Action<string, object?> variableWriter,
    Func<object?, string, Avm1Lookup> memberReader,
    Action<object?, string, object?> memberWriter,
    Func<string, IReadOnlyList<object?>, Avm1Lookup> functionCaller,
    Func<object?, string, IReadOnlyList<object?>, int, Avm1Lookup> methodCaller,
    Func<string, IReadOnlyList<object?>, Avm1Lookup> objectConstructor,
    Action<string> timelineLabelJumper,
    Action<Action>? commitActionWriter = null)
{
    private readonly Dictionary<string, object?> _variableWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<MemberKey, object?> _memberWrites = [];
    private readonly List<Action> _commitActions = [];

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

    public Avm1Lookup CallMethod(object? target, string name, IReadOnlyList<object?> arguments, int instructionOffset) =>
        methodCaller(target, name, arguments, instructionOffset);

    public Avm1Lookup ConstructObject(string name, IReadOnlyList<object?> arguments) =>
        objectConstructor(name, arguments);

    public void GotoLabel(string label) => timelineLabelJumper(label);

    public void OnCommit(Action action) => _commitActions.Add(action);

    public void Commit()
    {
        foreach (var (name, value) in _variableWrites)
            variableWriter(name, value);
        foreach (var (key, value) in _memberWrites)
            memberWriter(key.Target, key.Name, value);
        foreach (var action in _commitActions)
        {
            if (commitActionWriter is null)
                action();
            else
                commitActionWriter(action);
        }
    }

    private readonly record struct MemberKey(object? Target, string Name);
}

internal readonly record struct Avm1Lookup(bool Found, object? Value);
