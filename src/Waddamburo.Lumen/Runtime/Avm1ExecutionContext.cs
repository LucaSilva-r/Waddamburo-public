namespace Waddamburo.Lumen.Runtime;

internal sealed class Avm1ExecutionContext(
    IAvm1Host host,
    object owner,
    Avm1Value thisValue,
    Avm1LexicalScope? lexicalScope,
    object? timelineTarget,
    Avm1ExecutionContext? transactionParent)
{
    // Writes apply at once, as in Flash (a failing action keeps what it did). What waits for the call to end: clips it
    // creates (placed on commit; the transient tables let the script reach them before), host commands, playback.
    // Every write used to wait in a table, so every read probed those tables up the call chain first (a third of a
    // script's time); only the transient ones are probed now, and only when the chain has any.
    private List<Action>? _commitActions;
    private Dictionary<MemberKey, Avm1Value>? _transientMembers;
    private Dictionary<DisplayKey, object?>? _transientChildren;
    private bool _transientInChain = transactionParent?._transientInChain == true;

    public Avm1Lookup GetVariable(string name) => host.ReadVariable(this, name);

    public void SetVariable(string name, Avm1Value value)
    {
        for (var scope = LexicalScope; scope is not null; scope = scope.Parent)
            if (scope.ContainsOwn(name))
            {
                scope.Define(name, value);
                return;
            }
        if (TimelineTarget is not null)
            SetMember(Avm1Value.Of(TimelineTarget), name, value);
        else
            host.WriteVariable(this, name, value);
    }

    public void DefineLocal(string name, Avm1Value value)
    {
        if (LexicalScope is null)
            SetVariable(name, value);
        else
            LexicalScope.Define(name, value);
    }

    public Avm1LexicalScope? LexicalScope { get; } = lexicalScope;

    /// <summary>The clip whose script runs (its timeline for a function's call).</summary>
    public object Owner { get; } = owner;

    public Avm1Value ThisValue { get; } = thisValue;

    /// <summary>The execution this one commits into (null: commits to the player).</summary>
    public Avm1ExecutionContext? TransactionParent => transactionParent;

    public object? TimelineTarget { get; } = timelineTarget;
    public int InstructionOffset { get; set; }
    public bool? PendingPlayback { get; set; }

    public Avm1Lookup GetLexicalVariable(string name)
    {
        for (var scope = LexicalScope; scope is not null; scope = scope.Parent)
            if (scope.TryGetOwn(name, out var value))
                return new Avm1Lookup(true, value);
        return default;
    }

    public Avm1Lookup GetMember(Avm1Value target, string name)
    {
        if (_transientInChain)
        {
            var key = new MemberKey(identity(target), name);
            for (var context = this; context is not null; context = context.TransactionParent)
                if (context._transientMembers is not null && context._transientMembers.TryGetValue(key, out var transient))
                    return new Avm1Lookup(true, transient);
        }
        return host.ReadMember(this, target, name);
    }

    public void SetMember(Avm1Value target, string name, Avm1Value value) => host.WriteMember(this, target, name, value);

    // What a member's target is known by in the transient table: its string or object.
    private static object? identity(Avm1Value target) => target.Reference ?? target.Boxed;

    public Avm1Lookup CallFunction(string name, IReadOnlyList<Avm1Value> arguments) =>
        host.CallFunction(this, name, arguments);

    public Avm1Lookup CallMethod(Avm1Value target, string name, IReadOnlyList<Avm1Value> arguments, int instructionOffset) =>
        host.CallMethod(this, target, name, arguments, instructionOffset);

    public Avm1Lookup ConstructObject(string name, IReadOnlyList<Avm1Value> arguments) =>
        host.ConstructObject(this, name, arguments);

    public Avm1Lookup ConstructMethod(Avm1Value target, string name, IReadOnlyList<Avm1Value> arguments) =>
        host.ConstructMethod(this, target, name, arguments);

    public void GotoLabel(string label) => host.GotoLabel(this, label);

    public void GotoFrame(Avm1Value frame, bool play, int sceneBias) =>
        host.GotoFrame(this, frame, play, sceneBias);

    public void SendHostCommand(string command, string argument) =>
        OnCommit(() => host.HostCommand(this, command, argument));

    public Avm1Lookup CloneSprite(Avm1Value source, string name, int depth)
        => host.CloneSprite(this, source, name, depth);

    public void RemoveSprite(Avm1Value target) => host.RemoveSprite(this, target);

    public void SetTransientMember(object target, string name, Avm1Value value)
    {
        (_transientMembers ??= [])[new MemberKey(target, name)] = value;
        _transientInChain = true;
    }

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

    private readonly struct MemberKey(object? target, string name) : IEquatable<MemberKey>
    {
        public object? Target { get; } = target;
        public string Name { get; } = name;

        public bool Equals(MemberKey other) => string.Equals(Name, other.Name, StringComparison.Ordinal) && Equals(Target, other.Target);

        public override bool Equals(object? obj) => obj is MemberKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Target switch
        {
            IAvm1Identity known => known.AvmId,
            null => 0,
            var other => other.GetHashCode(),
        }, Name.GetHashCode());
    }
    private readonly record struct DisplayKey(object Parent, long Depth);
}

internal readonly record struct Avm1Lookup(bool Found, Avm1Value Value);
