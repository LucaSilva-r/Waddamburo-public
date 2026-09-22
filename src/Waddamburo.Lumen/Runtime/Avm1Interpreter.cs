using Waddamburo.Formats.Lmb;
using System.Globalization;

namespace Waddamburo.Lumen.Runtime;

internal static class Avm1Interpreter
{
    public static string DescribeUnsupported(Avm1CodeBlock code) => string.Join(
        ", ",
        code.Instructions
            .Where(instruction => !isSupportedInstruction(instruction))
            .Select(instruction => $"0x{instruction.Opcode:X2} at 0x{instruction.Offset:X}"));

    public static Avm1ExecutionStatus TryExecute(
        Avm1CodeBlock code,
        IReadOnlyList<LmbString> strings,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        out bool resultingPlaying)
    {
        var registers = Enumerable.Repeat<object?>(Avm1Undefined.Instance, limits.MaxRegisters).ToArray();
        return tryExecute(
            code,
            strings,
            initialPlaying,
            limits,
            context,
            registers,
            out resultingPlaying,
            out _);
    }

    public static Avm1ExecutionStatus TryExecuteFunction(
        Avm1FunctionValue function,
        IReadOnlyList<LmbString> strings,
        object? thisValue,
        IReadOnlyList<object?> arguments,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        out bool resultingPlaying,
        out object? returnValue)
    {
        var definition = function.Definition;
        if (definition.RegisterCount >= limits.MaxRegisters)
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        var registers = Enumerable.Repeat<object?>(Avm1Undefined.Instance, limits.MaxRegisters).ToArray();
        var nextRegister = 1;
        var argumentArray = new Avm1ArrayObject(arguments);
        var superValue = new Avm1SuperValue(
            thisValue,
            function.OwnerPrototype ?? function.GetProperty("prototype").Value as Avm1Object);
        if (function.IsFunction2 && (definition.Flags & 0x0001) != 0
            && !trySeedRegister(registers, ref nextRegister, thisValue))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0004) != 0
            && !trySeedRegister(registers, ref nextRegister, argumentArray))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0010) != 0
            && !trySeedRegister(registers, ref nextRegister, superValue))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0040) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_root").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0080) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_parent").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0100) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_global").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Undefined.Instance;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (!function.IsFunction2 || (definition.Flags & 0x0002) == 0)
            context.DefineLocal("this", thisValue);
        if (!function.IsFunction2 || (definition.Flags & 0x0008) == 0)
            context.DefineLocal("arguments", argumentArray);
        if (function.IsFunction2 && (definition.Flags & 0x0020) == 0)
            context.DefineLocal("super", superValue);
        for (var index = 0; index < definition.Parameters.Length; index++)
        {
            var parameter = definition.Parameters[index];
            var value = index < arguments.Count ? arguments[index] : Avm1Undefined.Instance;
            if (parameter.Register is > 0 and var register)
            {
                if (register >= registers.Length)
                {
                    resultingPlaying = initialPlaying;
                    returnValue = Avm1Undefined.Instance;
                    return Avm1ExecutionStatus.RegisterLimit;
                }
                registers[register] = value;
            }
            else
                context.DefineLocal(getString(strings, parameter.NameStringIndex), value);
        }
        return tryExecute(
            function.Body,
            strings,
            initialPlaying,
            limits,
            context,
            registers,
            out resultingPlaying,
            out returnValue);
    }

    private static Avm1ExecutionStatus tryExecute(
        Avm1CodeBlock code,
        IReadOnlyList<LmbString> strings,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        object?[] registers,
        out bool resultingPlaying,
        out object? returnValue)
    {
        resultingPlaying = initialPlaying;
        returnValue = Avm1Undefined.Instance;
        if (!isSupported(code))
            return Avm1ExecutionStatus.Unsupported;

        var instructions = code.Instructions.ToDictionary(instruction => instruction.Offset);
        var stack = new List<object?>();
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
            context.InstructionOffset = pc;
            switch (instruction.Opcode)
            {
                case 0x00: // End
                    resultingPlaying = playing;
                    return Avm1ExecutionStatus.Success;
                case 0x06: // Play
                    playing = true;
                    context.PendingPlayback = true;
                    break;
                case 0x07: // Stop
                    playing = false;
                    context.PendingPlayback = false;
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
                case 0x3F: // Modulo
                    if (!tryBinary(stack, (left, right) => toNumber(left) % toNumber(right)))
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
                    // Compiler-generated class guards can skip directly to a discard
                    // after initialization was already performed by package bootstrap.
                    // Empty discard is harmless; value-consuming operations stay strict.
                    _ = tryPop(stack, out _);
                    break;
                case 0x18: // ToInteger (truncate toward zero)
                    if (!tryUnary(stack, value => Math.Truncate(toNumber(value))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x1C: // GetVariable
                    if (!tryPop(stack, out var variableName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var variable = context.GetVariable(toAvmString(variableName));
                    if (!tryPush(stack, variable.Found ? variable.Value : Avm1Undefined.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x1D: // SetVariable
                    if (!tryPop(stack, out var variableValue) || !tryPop(stack, out variableName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.SetVariable(toAvmString(variableName), variableValue);
                    break;
                case 0x24: // CloneSprite
                    if (!tryPop(stack, out var cloneDepth)
                        || !tryPop(stack, out var cloneName)
                        || !tryPop(stack, out var cloneSource))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var depthNumber = toNumber(cloneDepth);
                    if (!double.IsFinite(depthNumber)
                        || depthNumber < 0
                        || depthNumber > int.MaxValue)
                        break;
                    context.CloneSprite(cloneSource, toAvmString(cloneName), (int)depthNumber);
                    break;
                case 0x25: // RemoveSprite
                    if (!tryPop(stack, out var removeTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.RemoveSprite(removeTarget);
                    break;
                case 0x3D: // CallFunction
                    if (!tryPop(stack, out var functionName)
                        || !tryPopArguments(stack, out var arguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var call = context.CallFunction(toAvmString(functionName), arguments);
                    if (!tryPush(stack, call.Found ? call.Value : Avm1Undefined.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x3E: // Return
                    if (!tryPop(stack, out returnValue))
                        return Avm1ExecutionStatus.StackUnderflow;
                    resultingPlaying = playing;
                    return Avm1ExecutionStatus.Success;
                case 0x3C: // DefineLocal
                    if (!tryPop(stack, out var localValue) || !tryPop(stack, out var localName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.DefineLocal(toAvmString(localName), localValue);
                    break;
                case 0x40: // NewObject
                    if (!tryPop(stack, out var constructorName)
                        || !tryPopArguments(stack, out var constructorArguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var constructed = context.ConstructObject(toAvmString(constructorName), constructorArguments);
                    if (!tryPush(
                        stack,
                        constructed.Found ? constructed.Value : Avm1Undefined.Instance,
                        limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x41: // DefineLocal2
                    if (!tryPop(stack, out localName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.DefineLocal(toAvmString(localName), Avm1Undefined.Instance);
                    break;
                case 0x42: // InitArray
                    if (!tryPopArguments(stack, out var arrayValues))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (!tryPush(stack, new Avm1ArrayObject(arrayValues), limits.MaxStackValues))
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
                    if (!tryPush(stack, member.Found ? member.Value : Avm1Undefined.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x4F: // SetMember
                    if (!tryPop(stack, out var memberValue)
                        || !tryPop(stack, out memberName)
                        || !tryPop(stack, out memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.SetMember(memberTarget, toAvmString(memberName), memberValue);
                    break;
                case 0x52: // CallMethod
                    if (!tryPop(stack, out var methodName)
                        || !tryPop(stack, out var methodTarget)
                        || !tryPopArguments(stack, out var methodArguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var resolvedMethodName = methodName is null or Avm1Undefined
                        ? ""
                        : toAvmString(methodName);
                    var methodCall = context.CallMethod(
                        methodTarget,
                        resolvedMethodName,
                        methodArguments,
                        instruction.Offset);
                    if (!tryPush(stack, methodCall.Found ? methodCall.Value : Avm1Undefined.Instance, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x60: // BitAnd
                    if (!tryBinary(stack, (left, right) => (double)(toInt32(left) & toInt32(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x61: // BitOr
                    if (!tryBinary(stack, (left, right) => (double)(toInt32(left) | toInt32(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x62: // BitXor
                    if (!tryBinary(stack, (left, right) => (double)(toInt32(left) ^ toInt32(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x63: // BitLShift
                    if (!tryBinary(stack, (left, right) => (double)(toInt32(left) << (toInt32(right) & 0x1f))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x64: // BitRShift
                    if (!tryBinary(stack, (left, right) => (double)(toInt32(left) >> (toInt32(right) & 0x1f))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x65: // BitURShift
                    if (!tryBinary(stack, (left, right) => (double)((uint)toInt32(left) >> (toInt32(right) & 0x1f))))
                        return Avm1ExecutionStatus.StackUnderflow;
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
                case 0x8C: // GoToLabel
                    var labelOperand = (Avm1StringIndexOperand)instruction.Operand!;
                    context.GotoLabel(getString(strings, labelOperand.StringIndex));
                    break;
                case 0x81: // GotoFrame uses a zero-based embedded frame index.
                    context.GotoFrame((double)((Avm1FrameOperand)instruction.Operand!).Frame + 1, playing, 0);
                    break;
                case 0x83: // Only explicit host commands; never open asset-provided URLs.
                    var command = (Avm1GetUrlOperand)instruction.Operand!;
                    context.SendHostCommand(command.Url[10..], command.Target);
                    break;
                case 0x9F: // GotoFrame2 (stack-based frame or label)
                    if (!tryPop(stack, out var frameValue))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var frameOperand = (Avm1GotoFrame2Operand)instruction.Operand!;
                    context.GotoFrame(frameValue, (frameOperand.Flags & 1) != 0, frameOperand.SceneBias ?? 0);
                    break;
                case 0x69: // Extends
                    if (!tryPop(stack, out var superClass) || !tryPop(stack, out var subClass))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var superPrototype = context.GetMember(superClass, "prototype");
                    if (subClass is Avm1Object
                        && superPrototype.Found
                        && superPrototype.Value is Avm1Object parentPrototype)
                    {
                        var prototype = new Avm1Object { Prototype = parentPrototype };
                        prototype.Properties["__constructor__"] = superClass;
                        context.SetMember(subClass, "prototype", prototype);
                    }
                    break;
                case 0x8E: // DefineFunction2
                case 0x9B: // DefineFunction
                    var definition = (Avm1FunctionOperand)instruction.Operand!;
                    var function = new Avm1FunctionValue(
                        definition,
                        instruction.Body!,
                        instruction.Opcode == 0x8E)
                    {
                        CapturedScope = context.LexicalScope,
                        DefinitionTarget = context.TimelineTarget,
                    };
                    var definedName = getString(strings, definition.NameStringIndex);
                    if (definedName.Length == 0)
                    {
                        if (!tryPush(stack, function, limits.MaxStackValues))
                            return Avm1ExecutionStatus.StackLimit;
                    }
                    else
                        context.SetVariable(definedName, function);
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

    private static bool trySeedRegister(object?[] registers, ref int nextRegister, object? value)
    {
        if (nextRegister >= registers.Length)
            return false;
        registers[nextRegister++] = value;
        return true;
    }

    private static bool isSupported(Avm1CodeBlock code) =>
        code.Instructions.Any(instruction => instruction.Opcode == 0x00)
        && code.Instructions.All(isSupportedInstruction);

    private static bool isSupportedInstruction(Avm1Instruction instruction) =>
        instruction.Opcode switch
        {
            0x00 or 0x06 or 0x07
                or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F or 0x12 or 0x17 or 0x18
                or 0x1C or 0x1D or 0x24 or 0x25 or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or 0x41 or 0x42
                or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B or 0x4C or 0x4D or 0x4E or 0x4F or 0x50 or 0x51 or 0x52
                or 0x60 or 0x61 or 0x62 or 0x63 or 0x64 or 0x65 or 0x66 or 0x67 or 0x69
                or 0x87 or 0x8C or 0x99 or 0x9D => true,
            0x8E or 0x9B => instruction.Operand is Avm1FunctionOperand && instruction.Body is not null,
            0x96 => instruction.Operand is Avm1PushOperand,
            0x9F => instruction.Operand is Avm1GotoFrame2Operand { Flags: <= 3 },
            0x81 => instruction.Operand is Avm1FrameOperand,
            0x83 => instruction.Operand is Avm1GetUrlOperand url
                && url.Url.StartsWith("FSCommand:", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

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
                    if (!tryPush(stack, Avm1Undefined.Instance, maxStackValues))
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
        null or Avm1Undefined => false,
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
        if (left is null or Avm1Undefined && right is null or Avm1Undefined)
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
        (Avm1Undefined, Avm1Undefined) => true,
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
        Avm1Undefined => double.NaN,
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
        Avm1Undefined => "undefined",
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

    private static string getString(IReadOnlyList<LmbString> strings, ushort index) =>
        index < strings.Count ? strings[index].Value : "";

    private static int toInt32(object? value)
    {
        var number = toNumber(value);
        if (!double.IsFinite(number) || number == 0)
            return 0;
        var integer = Math.Truncate(number);
        var modulo = integer % 4_294_967_296d;
        if (modulo < 0)
            modulo += 4_294_967_296d;
        return unchecked((int)(uint)modulo);
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
        for (var index = 0; index < count; index++)
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
