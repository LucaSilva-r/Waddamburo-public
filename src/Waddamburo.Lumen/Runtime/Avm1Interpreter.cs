using Waddamburo.Formats.Lmb;
using System.Globalization;

namespace Waddamburo.Lumen.Runtime;

internal static class Avm1Interpreter
{
    public static Avm1ExecutionStatus TryExecute(
        Avm1CodeBlock code,
        IReadOnlyList<LmbString> strings,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        out bool resultingPlaying)
    {
        resultingPlaying = initialPlaying;
        if (!isSupported(code))
            return Avm1ExecutionStatus.Unsupported;

        var instructions = code.Instructions.ToDictionary(instruction => instruction.Offset);
        var stack = new List<object?>();
        var registers = Enumerable.Repeat<object?>(UndefinedValue.Instance, limits.MaxRegisters).ToArray();
        var playing = initialPlaying;
        var pc = code.Offset;
        for (var remaining = limits.MaxInstructionsPerAction; remaining > 0; remaining--)
        {
            if (pc == code.Offset + code.Length)
            {
                resultingPlaying = playing;
                return Avm1ExecutionStatus.Success;
            }
            if (!instructions.TryGetValue(pc, out var instruction))
                return Avm1ExecutionStatus.InvalidControlFlow;

            var next = checked(pc + instruction.Length);
            switch (instruction.Opcode)
            {
                case 0x00: // End
                    resultingPlaying = playing;
                    return Avm1ExecutionStatus.Success;
                case 0x06: // Play
                    playing = true;
                    break;
                case 0x07: // Stop
                    playing = false;
                    break;
                case 0x0A: // Add
                    if (!tryBinary(stack, (left, right) => toNumber(left) + toNumber(right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0B: // Subtract
                    if (!tryBinary(stack, (left, right) => toNumber(left) - toNumber(right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0C: // Multiply
                    if (!tryBinary(stack, (left, right) => toNumber(left) * toNumber(right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0D: // Divide
                    if (!tryBinary(stack, (left, right) => toNumber(left) / toNumber(right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0E: // Equals
                case 0x49: // Equals2
                    if (!tryBinary(stack, (left, right) => looseEquals(left, right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0F: // Less
                case 0x48: // Less2
                    if (!tryBinary(stack, (left, right) => lessThan(left, right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x12: // Not
                    if (!tryPop(stack, out var condition))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (!tryPush(stack, !toBoolean(condition), limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x17: // Pop
                    if (!tryPop(stack, out _))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x1C: // GetVariable
                    if (!tryPop(stack, out var variableName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var variable = context.GetVariable(toAvmString(variableName));
                    if (!tryPush(stack, variable.Found ? variable.Value : UndefinedValue.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x1D: // SetVariable
                    if (!tryPop(stack, out var variableValue) || !tryPop(stack, out variableName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.SetVariable(toAvmString(variableName), variableValue);
                    break;
                case 0x3D: // CallFunction
                    if (!tryPop(stack, out var functionName)
                        || !tryPopArguments(stack, out var arguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var call = context.CallFunction(toAvmString(functionName), arguments);
                    if (!tryPush(stack, call.Found ? call.Value : UndefinedValue.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x4C: // PushDuplicate
                    if (!tryPeek(stack, out var duplicate))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (!tryPush(stack, duplicate, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x4D: // StackSwap
                    if (stack.Count < 2)
                        return Avm1ExecutionStatus.StackUnderflow;
                    (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                    break;
                case 0x4E: // GetMember
                    if (!tryPop(stack, out var memberName) || !tryPop(stack, out var memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var member = context.GetMember(memberTarget, toAvmString(memberName));
                    if (!tryPush(stack, member.Found ? member.Value : UndefinedValue.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x4F: // SetMember
                    if (!tryPop(stack, out var memberValue)
                        || !tryPop(stack, out memberName)
                        || !tryPop(stack, out memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.SetMember(memberTarget, toAvmString(memberName), memberValue);
                    break;
                case 0x47: // Add2
                    if (!tryBinary(stack, add2))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x4A: // ToNumber
                    if (!tryUnary(stack, value => toNumber(value)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x4B: // ToString
                    if (!tryUnary(stack, toAvmString))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x50: // Increment
                    if (!tryUnary(stack, value => toNumber(value) + 1))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x51: // Decrement
                    if (!tryUnary(stack, value => toNumber(value) - 1))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x66: // StrictEquals
                    if (!tryBinary(stack, (left, right) => strictEquals(left, right)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x67: // Greater
                    if (!tryBinary(stack, (left, right) => lessThan(right, left)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x96: // Push
                    var pushStatus = pushValues(stack, (Avm1PushOperand)instruction.Operand!, strings, registers, limits.MaxStackValues);
                    if (pushStatus != Avm1ExecutionStatus.Success)
                        return pushStatus;
                    break;
                case 0x87: // StoreRegister
                    if (!tryPeek(stack, out var registerValue))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var register = ((Avm1RegisterOperand)instruction.Operand!).Register;
                    if (register >= registers.Length)
                        return Avm1ExecutionStatus.RegisterLimit;
                    registers[register] = registerValue;
                    break;
                case 0x99: // Jump
                    next = ((Avm1BranchOperand)instruction.Operand!).Target;
                    break;
                case 0x9D: // If
                    if (!tryPop(stack, out var branchCondition))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (toBoolean(branchCondition))
                        next = ((Avm1BranchOperand)instruction.Operand!).Target;
                    break;
            }
            pc = next;
        }
        return Avm1ExecutionStatus.InstructionLimit;
    }

    private static bool isSupported(Avm1CodeBlock code) =>
        code.Instructions.Any(instruction => instruction.Opcode == 0x00)
        && code.Instructions.All(instruction => instruction.Opcode switch
        {
            0x00 or 0x06 or 0x07
                or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F or 0x12 or 0x17
                or 0x1C or 0x1D or 0x3D
                or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B or 0x4C or 0x4D or 0x4E or 0x4F or 0x50 or 0x51
                or 0x66 or 0x67 or 0x87 or 0x99 or 0x9D => true,
            0x96 => instruction.Operand is Avm1PushOperand,
            _ => false,
        });

    private static Avm1ExecutionStatus pushValues(
        List<object?> stack,
        Avm1PushOperand push,
        IReadOnlyList<LmbString> strings,
        object?[] registers,
        int maxStackValues)
    {
        foreach (var value in push.Values)
        {
            switch (value)
            {
                case Avm1PushStringValue text when text.StringIndex < strings.Count:
                    if (!tryPush(stack, strings[text.StringIndex].Value, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushFloatValue number:
                    if (!tryPush(stack, (double)number.Value, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushNullValue:
                    if (!tryPush(stack, null, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushUndefinedValue:
                    if (!tryPush(stack, UndefinedValue.Instance, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushBooleanValue boolean:
                    if (!tryPush(stack, boolean.Value, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushDoubleValue number:
                    if (!tryPush(stack, number.Value, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushIntegerValue number:
                    if (!tryPush(stack, (double)number.Value, maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case Avm1PushRegisterValue register when register.Register < registers.Length:
                    if (!tryPush(stack, registers[register.Register], maxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                default:
                    return Avm1ExecutionStatus.RegisterLimit;
            }
        }
        return Avm1ExecutionStatus.Success;
    }

    private static bool tryPush(List<object?> stack, object? value, int maxStackValues)
    {
        if (stack.Count >= maxStackValues)
            return false;
        stack.Add(value);
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

    private static object add2(object? left, object? right) =>
        left is string || right is string
            ? toAvmString(left) + toAvmString(right)
            : toNumber(left) + toNumber(right);

    private static bool lessThan(object? left, object? right)
    {
        if (left is string leftText && right is string rightText)
            return string.CompareOrdinal(leftText, rightText) < 0;
        var leftNumber = toNumber(left);
        var rightNumber = toNumber(right);
        return !double.IsNaN(leftNumber) && !double.IsNaN(rightNumber) && leftNumber < rightNumber;
    }

    private static bool looseEquals(object? left, object? right)
    {
        if (left is null or UndefinedValue && right is null or UndefinedValue)
            return true;
        if (left is bool)
            return looseEquals(toNumber(left), right);
        if (right is bool)
            return looseEquals(left, toNumber(right));
        if (left is double && right is string || left is string && right is double)
            return numericEquals(toNumber(left), toNumber(right));
        return strictEquals(left, right);
    }

    private static bool strictEquals(object? left, object? right) => (left, right) switch
    {
        (UndefinedValue, UndefinedValue) => true,
        (null, null) => true,
        (bool leftBoolean, bool rightBoolean) => leftBoolean == rightBoolean,
        (double leftNumber, double rightNumber) => numericEquals(leftNumber, rightNumber),
        (string leftText, string rightText) => leftText == rightText,
        _ => false,
    };

    private static bool numericEquals(double left, double right) =>
        !double.IsNaN(left) && !double.IsNaN(right) && left == right;

    private static double toNumber(object? value) => value switch
    {
        null => 0,
        UndefinedValue => double.NaN,
        bool boolean => boolean ? 1 : 0,
        double number => number,
        string text => parseNumber(text),
        _ => double.NaN,
    };

    private static double parseNumber(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return 0;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }

    private static string toAvmString(object? value) => value switch
    {
        null => "null",
        UndefinedValue => "undefined",
        bool boolean => boolean ? "true" : "false",
        double number when double.IsNaN(number) => "NaN",
        double number when double.IsPositiveInfinity(number) => "Infinity",
        double number when double.IsNegativeInfinity(number) => "-Infinity",
        double number => number.ToString("G15", CultureInfo.InvariantCulture),
        string text => text,
        _ => "[object Object]",
    };

    private static bool tryUnary(List<object?> stack, Func<object?, object?> operation)
    {
        if (!tryPop(stack, out var value))
            return false;
        stack.Add(operation(value));
        return true;
    }

    private static bool tryBinary(List<object?> stack, Func<object?, object?, object?> operation)
    {
        if (!tryPop(stack, out var right) || !tryPop(stack, out var left))
            return false;
        stack.Add(operation(left, right));
        return true;
    }

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

    private static bool tryPopArguments(List<object?> stack, out IReadOnlyList<object?> arguments)
    {
        if (!tryPop(stack, out var countValue))
        {
            arguments = [];
            return false;
        }
        var countNumber = toNumber(countValue);
        if (!double.IsFinite(countNumber) || countNumber < 0 || countNumber != Math.Truncate(countNumber)
            || countNumber > stack.Count || countNumber > int.MaxValue)
        {
            arguments = [];
            return false;
        }
        var count = (int)countNumber;
        var values = new object?[count];
        for (var index = count - 1; index >= 0; index--)
        {
            if (!tryPop(stack, out values[index]))
            {
                arguments = [];
                return false;
            }
        }
        arguments = values;
        return true;
    }

    private sealed class UndefinedValue
    {
        public static UndefinedValue Instance { get; } = new();
    }
}

internal enum Avm1ExecutionStatus
{
    Success,
    Unsupported,
    StackUnderflow,
    StackLimit,
    InstructionLimit,
    InvalidControlFlow,
    RegisterLimit,
}
