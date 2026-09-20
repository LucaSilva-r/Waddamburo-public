using Waddamburo.Formats.Lmb;

namespace Waddamburo.Lumen.Runtime;

internal static class Avm1Interpreter
{
    private const int InstructionBudget = 10_000;

    public static bool TryExecute(
        Avm1CodeBlock code,
        IReadOnlyList<LmbString> strings,
        bool initialPlaying,
        out bool resultingPlaying)
    {
        resultingPlaying = initialPlaying;
        if (!isSupported(code))
            return false;

        var instructions = code.Instructions.ToDictionary(instruction => instruction.Offset);
        var stack = new List<object?>();
        var playing = initialPlaying;
        var pc = code.Offset;
        for (var remaining = InstructionBudget; remaining > 0; remaining--)
        {
            if (pc == code.Offset + code.Length)
            {
                resultingPlaying = playing;
                return true;
            }
            if (!instructions.TryGetValue(pc, out var instruction))
                return false;

            var next = checked(pc + instruction.Length);
            switch (instruction.Opcode)
            {
                case 0x00: // End
                    resultingPlaying = playing;
                    return true;
                case 0x06: // Play
                    playing = true;
                    break;
                case 0x07: // Stop
                    playing = false;
                    break;
                case 0x12: // Not
                    if (!tryPop(stack, out var condition))
                        return false;
                    stack.Add(!toBoolean(condition));
                    break;
                case 0x17: // Pop
                    if (!tryPop(stack, out _))
                        return false;
                    break;
                case 0x4C: // PushDuplicate
                    if (!tryPeek(stack, out var duplicate))
                        return false;
                    stack.Add(duplicate);
                    break;
                case 0x4D: // StackSwap
                    if (stack.Count < 2)
                        return false;
                    (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                    break;
                case 0x96: // Push
                    if (!pushValues(stack, (Avm1PushOperand)instruction.Operand!, strings))
                        return false;
                    break;
                case 0x99: // Jump
                    next = ((Avm1BranchOperand)instruction.Operand!).Target;
                    break;
                case 0x9D: // If
                    if (!tryPop(stack, out var branchCondition))
                        return false;
                    if (toBoolean(branchCondition))
                        next = ((Avm1BranchOperand)instruction.Operand!).Target;
                    break;
            }
            pc = next;
        }
        return false;
    }

    private static bool isSupported(Avm1CodeBlock code) =>
        code.Instructions.Any(instruction => instruction.Opcode == 0x00)
        && code.Instructions.All(instruction => instruction.Opcode switch
        {
            0x00 or 0x06 or 0x07 or 0x12 or 0x17 or 0x4C or 0x4D or 0x99 or 0x9D => true,
            0x96 => ((Avm1PushOperand?)instruction.Operand)?.Values.All(value =>
                value is not Avm1PushRegisterValue) == true,
            _ => false,
        });

    private static bool pushValues(
        List<object?> stack,
        Avm1PushOperand push,
        IReadOnlyList<LmbString> strings)
    {
        foreach (var value in push.Values)
        {
            switch (value)
            {
                case Avm1PushStringValue text when text.StringIndex < strings.Count:
                    stack.Add(strings[text.StringIndex].Value);
                    break;
                case Avm1PushFloatValue number:
                    stack.Add((double)number.Value);
                    break;
                case Avm1PushNullValue:
                    stack.Add(null);
                    break;
                case Avm1PushUndefinedValue:
                    stack.Add(UndefinedValue.Instance);
                    break;
                case Avm1PushBooleanValue boolean:
                    stack.Add(boolean.Value);
                    break;
                case Avm1PushDoubleValue number:
                    stack.Add(number.Value);
                    break;
                case Avm1PushIntegerValue number:
                    stack.Add((double)number.Value);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool toBoolean(object? value) => value switch
    {
        null or UndefinedValue => false,
        bool boolean => boolean,
        double number => number != 0 && !double.IsNaN(number),
        string text => text.Length != 0,
        _ => true,
    };

    private static bool tryPop(List<object?> stack, out object? value)
    {
        if (stack.Count == 0)
        {
            value = null;
            return false;
        }
        value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return true;
    }

    private static bool tryPeek(List<object?> stack, out object? value)
    {
        if (stack.Count == 0)
        {
            value = null;
            return false;
        }
        value = stack[^1];
        return true;
    }

    private sealed class UndefinedValue
    {
        public static UndefinedValue Instance { get; } = new();
    }
}
