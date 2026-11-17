namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1ExecutionContext(
    IAvm1Host host,
    object owner,
    object? thisValue,
    Avm1LexicalScope? lexicalScope,
    object? timelineTarget,
    Avm1ExecutionContext? transactionParent)
{
    // Made on the first write: a context is built for every script call and most write nothing (six collections
    // per call were a large share of a song select tick's garbage).
    private Dictionary<string, object?>? _variableWrites;
    private Dictionary<LexicalKey, object?>? _lexicalWrites;
    private Dictionary<MemberKey, object?>? _memberWrites;
    private List<Action>? _commitActions;
    private Dictionary<MemberKey, object?>? _transientMembers;
    private Dictionary<DisplayKey, object?>? _transientChildren;

    public Avm1Lookup GetVariable(string name) =>
        _variableWrites is not null && _variableWrites.TryGetValue(name, out var value)
            ? new Avm1Lookup(true, value)
            : host.ReadVariable(this, name);

    public void SetVariable(string name, object? value)
    {
        if (tryFindLexicalOwner(name, out var owner))
            (_lexicalWrites ??= [])[new LexicalKey(owner, name)] = value;
        else if (TimelineTarget is not null)
            SetMember(TimelineTarget, name, value);
        else
            (_variableWrites ??= new(StringComparer.Ordinal))[name] = value;
    }

    public void DefineLocal(string name, object? value)
    {
        if (LexicalScope is null)
            SetVariable(name, value);
        else
            (_lexicalWrites ??= [])[new LexicalKey(LexicalScope, name)] = value;
    }

    public Avm1LexicalScope? LexicalScope { get; } = lexicalScope;

    /// <summary>The clip whose script runs (its timeline for a function's call).</summary>
    public object Owner { get; } = owner;

    public object? ThisValue { get; } = thisValue;

    /// <summary>The execution this one commits into (null: commits to the player).</summary>
    public Avm1ExecutionContext? TransactionParent => transactionParent;

    public object? TimelineTarget { get; } = timelineTarget;
    public int InstructionOffset { get; set; }
    public bool? PendingPlayback { get; set; }

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
        _transientMembers is not null && _transientMembers.TryGetValue(new MemberKey(target, name), out var transient)
            ? new Avm1Lookup(true, transient)
            : _memberWrites is not null && _memberWrites.TryGetValue(new MemberKey(target, name), out var value)
            ? new Avm1Lookup(true, value)
            : host.ReadMember(this, target, name);

    public void SetMember(object? target, string name, object? value) =>
        (_memberWrites ??= [])[new MemberKey(target, name)] = value;

    public Avm1Lookup CallFunction(string name, IReadOnlyList<object?> arguments) =>
        host.CallFunction(this, name, arguments);

    public Avm1Lookup CallMethod(object? target, string name, IReadOnlyList<object?> arguments, int instructionOffset) =>
        host.CallMethod(this, target, name, arguments, instructionOffset);

    public Avm1Lookup ConstructObject(string name, IReadOnlyList<object?> arguments) =>
        host.ConstructObject(this, name, arguments);

    public Avm1Lookup ConstructMethod(object? target, string name, IReadOnlyList<object?> arguments) =>
        host.ConstructMethod(this, target, name, arguments);

    public void GotoLabel(string label) => host.GotoLabel(this, label);

    public void GotoFrame(object? frame, bool play, int sceneBias) =>
        host.GotoFrame(this, frame, play, sceneBias);

    public void SendHostCommand(string command, string argument) =>
        OnCommit(() => host.HostCommand(this, command, argument));

    public Avm1Lookup CloneSprite(object? source, string name, int depth)
        => host.CloneSprite(this, source, name, depth);

    public void RemoveSprite(object? target) => host.RemoveSprite(this, target);

    public void SetTransientMember(object target, string name, object? value) =>
        (_transientMembers ??= [])[new MemberKey(target, name)] = value;

    public void SetTransientChild(object parent, long depth, object? value) =>
        (_transientChildren ??= [])[new DisplayKey(parent, depth)] = value;

    public long GetNextHighestDepth(object parent, IEnumerable<long> committedDepths)
    {
        var depths = committedDepths.Where(static depth => depth >= 0).ToHashSet();
        applyTransientChildren(parent, depths);
        return depths.Count == 0 ? 0 : depths.Max() + 1;
    }

    public void OnCommit(Action action) => (_commitActions ??= []).Add(action);

    public void Commit()
    {
        if (transactionParent is not null)
        {
            if (_transientMembers is not null)
                foreach (var (key, value) in _transientMembers)
                    transactionParent.SetTransientMember(key.Target!, key.Name, value);
            if (_transientChildren is not null)
                foreach (var (key, value) in _transientChildren)
                    transactionParent.SetTransientChild(key.Parent, key.Depth, value);
        }
        if (_lexicalWrites is not null)
            foreach (var (key, value) in _lexicalWrites)
                key.Scope.Define(key.Name, value);
        if (_variableWrites is not null)
            foreach (var (name, value) in _variableWrites)
                host.WriteVariable(this, name, value);
        if (_memberWrites is not null)
            foreach (var (key, value) in _memberWrites)
                host.WriteMember(this, key.Target, key.Name, value);
        if (_commitActions is null)
            return;
        foreach (var action in _commitActions)
        {
            if (transactionParent is null)
                action();
            else
                transactionParent.OnCommit(action);
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
        _lexicalWrites?.ContainsKey(key) == true || commitParentHasLexicalWrite(key);

    private bool commitParentHasLexicalWrite(LexicalKey key) =>
        transactionParent?.hasLexicalWrite(key) == true;

    private bool tryGetLexicalWrite(LexicalKey key, out object? value)
    {
        if (_lexicalWrites is not null && _lexicalWrites.TryGetValue(key, out value))
            return true;
        if (transactionParent is not null)
            return transactionParent.tryGetLexicalWrite(key, out value);
        value = null;
        return false;
    }

    private void applyTransientChildren(object parent, HashSet<long> depths)
    {
        transactionParent?.applyTransientChildren(parent, depths);
        if (_transientChildren is null)
            return;
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
    private readonly record struct LexicalKey(Avm1LexicalScope Scope, string Name)
    {
        public bool Equals(LexicalKey other) => ReferenceEquals(Scope, other.Scope) && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Scope.Id, string.GetHashCode(Name, StringComparison.Ordinal));
    }
}

internal readonly record struct Avm1Lookup(bool Found, object? Value);
