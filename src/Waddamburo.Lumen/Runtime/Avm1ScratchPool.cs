namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// The register file and operand stack of one AVM1 execution, reused: every frame action and enter-frame handler
/// used to allocate a fresh 256-entry register array and stack, most of a song select tick's garbage. Registers and
/// the stack live only for their execution (AVM1 closures capture scopes, not registers), so a nested call rents its
/// own and each goes back when its execution ends.
/// </summary>
internal static class Avm1ScratchPool
{
    [ThreadStatic] private static Stack<object?[]>? s_registers;
    [ThreadStatic] private static Stack<List<object?>>? s_stacks;

    public static RegisterLease RentRegisters(int count)
    {
        var pool = s_registers ??= new();
        var registers = pool.TryPop(out var reused) && reused.Length == count ? reused : new object?[count];
        Array.Fill(registers, Avm1Undefined.Instance);
        return new RegisterLease(registers);
    }

    public static StackLease RentStack() => new((s_stacks ??= new()).TryPop(out var reused) ? reused : []);

    public readonly struct RegisterLease(object?[] registers) : IDisposable
    {
        public object?[] Registers { get; } = registers;

        public void Dispose()
        {
            Array.Clear(Registers); // keep nothing alive from the finished call
            (s_registers ??= new()).Push(Registers);
        }
    }

    public readonly struct StackLease(List<object?> stack) : IDisposable
    {
        public List<object?> Stack { get; } = stack;

        public void Dispose()
        {
            Stack.Clear();
            (s_stacks ??= new()).Push(Stack);
        }
    }
}
