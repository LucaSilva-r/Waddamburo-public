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
    Func<object?, string, int, Avm1Lookup> spriteCloner,
    Action<object?> spriteRemover,
    Avm1LexicalScope? lexicalScope,
    object? timelineTarget,
    Avm1ExecutionContext? transactionParent,
    Action<Action>? commitActionWriter = null,
    Action<object?, bool, int>? timelineFrameJumper = null,
    Action<string, string>? hostCommand = null)
{
    private readonly Dictionary<string, object?> _variableWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<LexicalKey, object?> _lexicalWrites = [];
    private readonly Dictionary<MemberKey, object?> _memberWrites = [];
    private readonly List<Action> _commitActions = [];
    private readonly Dictionary<MemberKey, object?> _transientMembers = [];
    private readonly Dictionary<DisplayKey, object?> _transientChildren = [];

    public Avm1Lookup GetVariable(string name) =>
        _variableWrites.TryGetValue(name, out var value)
            ? new Avm1Lookup(true, value)
            : variableReader(name);

    public void SetVariable(string name, object? value)
    {
        if (tryFindLexicalOwner(name, out var owner))
            _lexicalWrites[new LexicalKey(owner, name)] = value;
        else if (TimelineTarget is not null)
            SetMember(TimelineTarget, name, value);
        else
            _variableWrites[name] = value;
    }

    public void DefineLocal(string name, object? value)
    {
        if (LexicalScope is null)
            SetVariable(name, value);
        else
            _lexicalWrites[new LexicalKey(LexicalScope, name)] = value;
    }

    public Avm1LexicalScope? LexicalScope { get; } = lexicalScope;

    public object? TimelineTarget { get; } = timelineTarget;
    public int InstructionOffset { get; set; }

    public Avm1Lookup GetLexicalVariable(string name)
    {
        for (var scope = LexicalScope; scope is not null; scope = scope.Parent)
        {
            var key = new LexicalKey(scope, name);
            if (tryGetLexicalWrite(key, out var pending))
                return new Avm1Lookup(true, pending);
            if (scope.TryGetOwn(name, out var value))
                return new Avm1Lookup(true, value);
        }
        return default;
    }

    public Avm1Lookup GetMember(object? target, string name) =>
        _transientMembers.TryGetValue(new MemberKey(target, name), out var transient)
            ? new Avm1Lookup(true, transient)
            : _memberWrites.TryGetValue(new MemberKey(target, name), out var value)
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

    public void GotoFrame(object? frame, bool play, int sceneBias) =>
        timelineFrameJumper?.Invoke(frame, play, sceneBias);

    public void SendHostCommand(string command, string argument) =>
        OnCommit(() => hostCommand?.Invoke(command, argument));

    public Avm1Lookup CloneSprite(object? source, string name, int depth)
        => spriteCloner(source, name, depth);

    public void RemoveSprite(object? target) => spriteRemover(target);

    public void SetTransientMember(object target, string name, object? value) =>
        _transientMembers[new MemberKey(target, name)] = value;

    public void SetTransientChild(object parent, long depth, object? value) =>
        _transientChildren[new DisplayKey(parent, depth)] = value;

    public long GetNextHighestDepth(object parent, IEnumerable<long> committedDepths)
    {
        var depths = committedDepths.Where(static depth => depth >= 0).ToHashSet();
        applyTransientChildren(parent, depths);
        return depths.Count == 0 ? 0 : depths.Max() + 1;
    }

    public void OnCommit(Action action) => _commitActions.Add(action);

    public void Commit()
    {
        if (transactionParent is not null)
        {
            foreach (var (key, value) in _transientMembers)
                transactionParent.SetTransientMember(key.Target!, key.Name, value);
            foreach (var (key, value) in _transientChildren)
                transactionParent.SetTransientChild(key.Parent, key.Depth, value);
        }
        foreach (var (key, value) in _lexicalWrites)
            key.Scope.Define(key.Name, value);
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

    private bool tryFindLexicalOwner(string name, out Avm1LexicalScope owner)
    {
        for (var scope = LexicalScope; scope is not null; scope = scope.Parent)
        {
            var key = new LexicalKey(scope, name);
            if (hasLexicalWrite(key) || scope.ContainsOwn(name))
            {
                owner = scope;
                return true;
            }
        }
        owner = null!;
        return false;
    }

    private bool hasLexicalWrite(LexicalKey key) =>
        _lexicalWrites.ContainsKey(key) || commitParentHasLexicalWrite(key);

    private bool commitParentHasLexicalWrite(LexicalKey key) =>
        transactionParent?.hasLexicalWrite(key) == true;

    private bool tryGetLexicalWrite(LexicalKey key, out object? value)
    {
        if (_lexicalWrites.TryGetValue(key, out value))
            return true;
        if (transactionParent is not null)
            return transactionParent.tryGetLexicalWrite(key, out value);
        value = null;
        return false;
    }

    private void applyTransientChildren(object parent, HashSet<long> depths)
    {
        transactionParent?.applyTransientChildren(parent, depths);
        foreach (var (key, value) in _transientChildren)
        {
            if (!ReferenceEquals(key.Parent, parent))
                continue;
            if (value is null)
                depths.Remove(key.Depth);
            else if (key.Depth >= 0)
                depths.Add(key.Depth);
        }
    }

    private readonly record struct MemberKey(object? Target, string Name);
    private readonly record struct DisplayKey(object Parent, long Depth);
    private readonly record struct LexicalKey(Avm1LexicalScope Scope, string Name);
}

internal readonly record struct Avm1Lookup(bool Found, object? Value);
