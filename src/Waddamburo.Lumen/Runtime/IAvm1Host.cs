namespace Waddamburo.Lumen.Runtime;

/// <summary>What an AVM1 execution asks its player for (variables, members, calls, timeline jumps); the call's own
/// state (its clip, this, the enclosing transaction) is on the <see cref="Avm1ExecutionContext"/> passed along.</summary>
internal interface IAvm1Host
{
    Avm1Lookup ReadVariable(Avm1ExecutionContext context, string name);
    void WriteVariable(Avm1ExecutionContext context, string name, object? value);
    Avm1Lookup ReadMember(Avm1ExecutionContext context, object? target, string name);
    void WriteMember(Avm1ExecutionContext context, object? target, string name, object? value);
    Avm1Lookup CallFunction(Avm1ExecutionContext context, string name, IReadOnlyList<object?> arguments);
    Avm1Lookup CallMethod(Avm1ExecutionContext context, object? target, string name, IReadOnlyList<object?> arguments, int instructionOffset);
    Avm1Lookup ConstructObject(Avm1ExecutionContext context, string name, IReadOnlyList<object?> arguments);
    void GotoLabel(Avm1ExecutionContext context, string label);
    Avm1Lookup CloneSprite(Avm1ExecutionContext context, object? source, string name, int depth);
    void RemoveSprite(Avm1ExecutionContext context, object? target);
    void GotoFrame(Avm1ExecutionContext context, object? value, bool play, int bias);
    void HostCommand(Avm1ExecutionContext context, string command, string argument);
    Avm1Lookup ConstructMethod(Avm1ExecutionContext context, object? target, string name, IReadOnlyList<object?> arguments);
}
