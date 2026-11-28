using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using System.Globalization;

namespace Waddamburo.Lumen.Runtime;

internal static class Avm1Interpreter
{
    // Flash 4 property indices (GetProperty / SetProperty).
    private static readonly string[] PropertyNames =
    [
        "_x", "_y", "_xscale", "_yscale", "_currentframe", "_totalframes", "_alpha", "_visible",
        "_width", "_height", "_rotation", "_target", "_framesloaded", "_name", "_droptarget", "_url",
        "_highquality", "_focusrect", "_soundbuftime", "_quality", "_xmouse", "_ymouse",
    ];

    private static string? propertyName(double index) =>
        index >= 0 && index < PropertyNames.Length ? PropertyNames[(int)index] : null;

    // An empty target path is the current timeline (not `this`, which a host-invoked callback lacks).
    private static Avm1Value resolveTarget(Avm1ExecutionContext context, Avm1Value target)
    {
        var path = toAvmString(target);
        if (path.Length == 0 && context.TimelineTarget is { } timeline)
            return Avm1Value.Of(timeline);
        var lookup = context.GetVariable(path.Length == 0 ? "this" : path);
        return lookup.Found ? lookup.Value : Avm1Value.Undefined;
    }

    public static string DescribeUnsupported(Avm1CodeBlock code) => string.Join(
        ", ",
        code.Instructions
            .Where(instruction => !isSupportedInstruction(instruction))
            .Select(instruction => $"0x{instruction.Opcode:X2} at 0x{instruction.Offset:X}"));

    public static Avm1ExecutionStatus TryExecute(
        Avm1CodeBlock code,
        ImmutableArray<LmbString> strings,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        out bool resultingPlaying)
    {
        using var registerLease = Avm1ScratchPool.RentRegisters(Math.Min(limits.MaxRegisters, facts(code).Registers));
        var registers = registerLease.Registers;
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
        ImmutableArray<LmbString> strings,
        Avm1Value thisValue,
        IReadOnlyList<Avm1Value> arguments,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        out bool resultingPlaying,
        out Avm1Value returnValue)
    {
        var definition = function.Definition;
        if (definition.RegisterCount >= limits.MaxRegisters)
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        // The register file covers what the function can touch: its declared count, its parameters' registers, the
        // preloaded ones (this, arguments, super, _root, _parent, _global after register 0) and its body's.
        var registerCount = Math.Max(Math.Max(definition.RegisterCount, facts(function.Body).Registers), 8);
        foreach (var parameter in definition.Parameters)
            if (parameter.Register is { } parameterRegister)
                registerCount = Math.Max(registerCount, parameterRegister + 1);
        using var registerLease = Avm1ScratchPool.RentRegisters(Math.Min(limits.MaxRegisters, registerCount));
        var registers = registerLease.Registers;
        var nextRegister = 1;
        // `arguments` and `super` are built only when the function uses them: a DefineFunction2 says so in its flags
        // (preload 0x04/0x10, or not suppressed 0x08/0x20), and most calls need neither (a list copy and an object each).
        var flags = function.IsFunction2 ? definition.Flags : 0;
        var wantsArguments = !function.IsFunction2 || (flags & 0x0004) != 0 || (flags & 0x0008) == 0;
        var wantsSuper = function.IsFunction2 && ((flags & 0x0010) != 0 || (flags & 0x0020) == 0);
        var argumentArray = wantsArguments ? new Avm1ArrayObject(arguments) : null;
        var superValue = wantsSuper ? new Avm1SuperValue(
            thisValue,
            function.OwnerPrototype ?? function.GetProperty("prototype").Value.Object as Avm1Object) : null;
        if (function.IsFunction2 && (definition.Flags & 0x0001) != 0
            && !trySeedRegister(registers, ref nextRegister, thisValue))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0004) != 0
            && !trySeedRegister(registers, ref nextRegister, argumentArray))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0010) != 0
            && !trySeedRegister(registers, ref nextRegister, Avm1Value.Of(superValue)))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0040) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_root").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0080) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_parent").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (function.IsFunction2 && (definition.Flags & 0x0100) != 0
            && !trySeedRegister(registers, ref nextRegister, context.GetVariable("_global").Value))
        {
            resultingPlaying = initialPlaying;
            returnValue = Avm1Value.Undefined;
            return Avm1ExecutionStatus.RegisterLimit;
        }
        if (!function.IsFunction2 || (definition.Flags & 0x0002) == 0)
            context.DefineLocal("this", thisValue);
        if (!function.IsFunction2 || (definition.Flags & 0x0008) == 0)
            context.DefineLocal("arguments", argumentArray);
        if (function.IsFunction2 && (definition.Flags & 0x0020) == 0)
            context.DefineLocal("super", Avm1Value.Of(superValue));
        for (var index = 0; index < definition.Parameters.Length; index++)
        {
            var parameter = definition.Parameters[index];
            var value = index < arguments.Count ? arguments[index] : Avm1Value.Undefined;
            if (parameter.Register is > 0 and var register)
            {
                if (register >= registers.Length)
                {
                    resultingPlaying = initialPlaying;
                    returnValue = Avm1Value.Undefined;
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

    // The index of the instruction at pc, or -1.
    private static int findInstruction(ImmutableArray<Avm1Instruction> instructions, int pc)
    {
        int low = 0, high = instructions.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var offset = instructions[middle].Offset;
            if (offset == pc)
                return middle;
            if (offset < pc)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return -1;
    }

    private static Avm1ExecutionStatus tryExecute(
        Avm1CodeBlock code,
        ImmutableArray<LmbString> strings,
        bool initialPlaying,
        LumenRuntimeLimits limits,
        Avm1ExecutionContext context,
        Avm1Value[] registers,
        out bool resultingPlaying,
        out Avm1Value returnValue)
    {
        resultingPlaying = initialPlaying;
        returnValue = Avm1Value.Undefined;
        if (!isSupported(code))
            return Avm1ExecutionStatus.Unsupported;

        // The instructions are in offset order: the next one is usually the following entry, a jump binary-searches
        // (a dictionary built per call cost more than the script itself).
        var instructions = code.Instructions;
        var index = 0;
        using var stackLease = Avm1ScratchPool.RentStack();
        var stack = stackLease.Stack;
        var playing = initialPlaying;
        var pc = code.Offset;
        for (var remaining = limits.MaxInstructionsPerAction; remaining > 0; remaining--)
        {
            if (pc == code.Offset + code.Length)
            {
                resultingPlaying = playing;
                return Avm1ExecutionStatus.Success;
            }
            if (index >= instructions.Length || instructions[index].Offset != pc)
            {
                index = findInstruction(instructions, pc);
                if (index < 0)
                    return Avm1ExecutionStatus.InvalidControlFlow;
            }
            var instruction = instructions[index];

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
                    if (!tryBinary(stack, (left, right) => val(toNumber(left) + toNumber(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0B: // Subtract
                    if (!tryBinary(stack, (left, right) => val(toNumber(left) - toNumber(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0C: // Multiply
                    if (!tryBinary(stack, (left, right) => val(toNumber(left) * toNumber(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0D: // Divide
                    if (!tryBinary(stack, (left, right) => val(toNumber(left) / toNumber(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x3F: // Modulo
                    if (!tryBinary(stack, (left, right) => val(toNumber(left) % toNumber(right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0E: // Equals
                case 0x49: // Equals2
                    if (!tryBinary(stack, (left, right) => val(looseEquals(left, right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x0F: // Less
                case 0x48: // Less2
                    if (!tryBinary(stack, (left, right) => val(lessThan(left, right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x12: // Not
                    if (!tryPop(stack, out var condition))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (!tryPush(stack, val(!toBoolean(condition)), limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x17: // Pop
                    // Compiler-generated class guards can skip directly to a discard
                    // after initialization was already performed by package bootstrap.
                    // Empty discard is harmless; value-consuming operations stay strict.
                    _ = tryPop(stack, out _);
                    break;
                case 0x26: // Trace: authored debug output; the value is discarded
                    if (!tryPop(stack, out _))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x18: // ToInteger (truncate toward zero)
                    if (!tryUnary(stack, value => val(Math.Truncate(toNumber(value)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x1C: // GetVariable
                    if (!tryPop(stack, out var variableName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var variable = context.GetVariable(toAvmString(variableName));
                    if (!tryPush(stack, variable.Found ? variable.Value : Avm1Value.Undefined, limits.MaxStackValues))
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
                    if (!tryPush(stack, call.Found ? call.Value : Avm1Value.Undefined, limits.MaxStackValues))
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
                        constructed.Found ? constructed.Value : Avm1Value.Undefined,
                        limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x53: // NewMethod
                    if (!tryPop(stack, out var methodConstructorName)
                        || !tryPop(stack, out var constructorOwner)
                        || !tryPopArguments(stack, out var methodConstructorArguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var methodObject = context.ConstructMethod(constructorOwner,
                        toAvmString(methodConstructorName), methodConstructorArguments);
                    if (!tryPush(stack, methodObject.Found ? methodObject.Value : Avm1Value.Undefined,
                        limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x41: // DefineLocal2
                    if (!tryPop(stack, out localName))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.DefineLocal(toAvmString(localName), Avm1Value.Undefined);
                    break;
                case 0x42: // InitArray
                    if (!tryPopArguments(stack, out var arrayValues))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (!tryPush(stack, new Avm1ArrayObject(arrayValues), limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x43: // InitObject: the count, then each property's value above its name
                    if (!tryPop(stack, out var initCountValue))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var initCount = toNumber(initCountValue);
                    if (!double.IsFinite(initCount) || initCount < 0 || initCount * 2 > stack.Count)
                        return Avm1ExecutionStatus.StackUnderflow;
                    var initialized = new Avm1Object();
                    for (var initIndex = 0; initIndex < (int)initCount; initIndex++)
                    {
                        if (!tryPop(stack, out var initialValue) || !tryPop(stack, out var initialName))
                            return Avm1ExecutionStatus.StackUnderflow;
                        initialized.Properties[toAvmString(initialName)] = initialValue;
                    }
                    if (!tryPush(stack, initialized, limits.MaxStackValues))
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
                case 0x22: // GetProperty (Flash 4): target path, property index
                    if (!tryPop(stack, out var propertyIndex) || !tryPop(stack, out var propertyTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var property = propertyName(toNumber(propertyIndex)) is { } getName
                        ? context.GetMember(resolveTarget(context, propertyTarget), getName) : default;
                    if (!tryPush(stack, property.Found ? property.Value : Avm1Value.Undefined, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x23: // SetProperty (Flash 4): target path, property index, value
                    if (!tryPop(stack, out var propertyValue)
                        || !tryPop(stack, out propertyIndex)
                        || !tryPop(stack, out propertyTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    if (propertyName(toNumber(propertyIndex)) is { } setName)
                        context.SetMember(resolveTarget(context, propertyTarget), setName, propertyValue);
                    break;
                case 0x4E: // GetMember
                    if (!tryPop(stack, out var memberName) || !tryPop(stack, out var memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var member = context.GetMember(memberTarget, toAvmString(memberName));
                    if (!tryPush(stack, member.Found ? member.Value : Avm1Value.Undefined, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x4F: // SetMember
                    if (!tryPop(stack, out var memberValue)
                        || !tryPop(stack, out memberName)
                        || !tryPop(stack, out memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    context.SetMember(memberTarget, toAvmString(memberName), memberValue);
                    break;
                case 0x3A: // Delete
                    if (!tryPop(stack, out memberName) || !tryPop(stack, out memberTarget))
                        return Avm1ExecutionStatus.StackUnderflow;
                    // ponytail: the member stays, undefined: nothing here enumerates members after a delete.
                    context.SetMember(memberTarget, toAvmString(memberName), Avm1Value.Undefined);
                    if (!tryPush(stack, true, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x52: // CallMethod
                    if (!tryPop(stack, out var methodName)
                        || !tryPop(stack, out var methodTarget)
                        || !tryPopArguments(stack, out var methodArguments))
                        return Avm1ExecutionStatus.StackUnderflow;
                    var resolvedMethodName = methodName.IsNullish
                        ? ""
                        : toAvmString(methodName);
                    var methodCall = context.CallMethod(
                        methodTarget,
                        resolvedMethodName,
                        methodArguments,
                        instruction.Offset);
                    if (!tryPush(stack, methodCall.Found ? methodCall.Value : Avm1Value.Undefined, limits.MaxStackValues))
                        return Avm1ExecutionStatus.StackLimit;
                    break;
                case 0x60: // BitAnd
                    if (!tryBinary(stack, (left, right) => val((double)(toInt32(left) & toInt32(right)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x61: // BitOr
                    if (!tryBinary(stack, (left, right) => val((double)(toInt32(left) | toInt32(right)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x62: // BitXor
                    if (!tryBinary(stack, (left, right) => val((double)(toInt32(left) ^ toInt32(right)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x63: // BitLShift
                    if (!tryBinary(stack, (left, right) => val((double)(toInt32(left) << (toInt32(right) & 0x1f)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x64: // BitRShift
                    if (!tryBinary(stack, (left, right) => val((double)(toInt32(left) >> (toInt32(right) & 0x1f)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x65: // BitURShift
                    if (!tryBinary(stack, (left, right) => val((double)((uint)toInt32(left) >> (toInt32(right) & 0x1f)))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x47: // Add2
                    if (!tryBinary(stack, add2))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x4A: // ToNumber
                    if (!tryUnary(stack, value => val(toNumber(value))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x4B: // ToString
                    if (!tryUnary(stack, static value => toAvmString(value)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x50: // Increment
                    if (!tryUnary(stack, value => val(toNumber(value) + 1)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x51: // Decrement
                    if (!tryUnary(stack, value => val(toNumber(value) - 1)))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x66: // StrictEquals
                    if (!tryBinary(stack, (left, right) => val(strictEquals(left, right))))
                        return Avm1ExecutionStatus.StackUnderflow;
                    break;
                case 0x67: // Greater
                    if (!tryBinary(stack, (left, right) => val(lessThan(right, left))))
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
                    if (subClass.Object is Avm1Object
                        && superPrototype.Found
                        && superPrototype.Value.Object is Avm1Object parentPrototype)
                    {
                        var prototype = new Avm1Object { Prototype = parentPrototype };
                        prototype.Properties["__constructor__"] = superClass;
                        context.SetMember(subClass, "prototype", prototype);
                    }
                    break;
                case 0x8E: // DefineFunction2
                case 0x9B: // DefineFunction
                    var definition = (Avm1FunctionOperand)instruction.Operand!;
                    if (context.LexicalScope is { } closing)
                        closing.Captured = true;
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
            index++;
        }
        return Avm1ExecutionStatus.InstructionLimit;
    }

    private static bool trySeedRegister(Avm1Value[] registers, ref int nextRegister, Avm1Value value)
    {
        if (nextRegister >= registers.Length)
            return false;
        registers[nextRegister++] = value;
        return true;
    }

    private static bool isSupported(Avm1CodeBlock code) => facts(code).Supported;

    // What every execution of a code block would otherwise work out again: whether it runs, and the registers it
    // can reference (StoreRegister, register pushes), so the register file is that small.
    private sealed record CodeFacts(bool Supported, int Registers);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avm1CodeBlock, CodeFacts> s_facts = [];

    private static CodeFacts facts(Avm1CodeBlock code) => s_facts.GetValue(code, static code =>
    {
        var registers = 0;
        foreach (var instruction in code.Instructions)
        {
            if (instruction.Operand is Avm1RegisterOperand store)
                registers = Math.Max(registers, store.Register + 1);
            else if (instruction.Operand is Avm1PushOperand push)
                foreach (var value in push.Values)
                    if (value.Type == 4)
                        registers = Math.Max(registers, value.Register + 1);
        }
        return new CodeFacts(
            code.Instructions.Any(instruction => instruction.Opcode == 0x00) && code.Instructions.All(isSupportedInstruction),
            registers);
    });

    private static bool isSupportedInstruction(Avm1Instruction instruction) =>
        instruction.Opcode switch
        {
            0x00 or 0x06 or 0x07
                or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F or 0x12 or 0x17 or 0x18 or 0x22 or 0x23 or 0x26
                or 0x1C or 0x1D or 0x24 or 0x25 or 0x3A or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or 0x41 or 0x42 or 0x43
                or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B or 0x4C or 0x4D or 0x4E or 0x4F or 0x50 or 0x51 or 0x52 or 0x53
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
        List<Avm1Value> stack,
        Avm1PushOperand push,
        ImmutableArray<LmbString> strings,
        Avm1Value[] registers,
        int maxStackValues)
    {
        foreach (var value in push.Values)
        {
            Avm1Value pushed;
            switch (value.Type)
            {
                case 0 or 8 or 9 when value.StringIndex < strings.Length:
                    pushed = strings[value.StringIndex].Value;
                    break;
                case 1 or 6 or 7:
                    pushed = value.Number;
                    break;
                case 2:
                    pushed = Avm1Value.Null;
                    break;
                case 3:
                    pushed = Avm1Value.Undefined;
                    break;
                case 5:
                    pushed = value.Boolean;
                    break;
                case 4 when value.Register < registers.Length:
                    pushed = registers[value.Register];
                    break;
                default:
                    return Avm1ExecutionStatus.RegisterLimit;
            }
            if (!tryPush(stack, pushed, maxStackValues))
                return Avm1ExecutionStatus.StackLimit;
        }
        return Avm1ExecutionStatus.Success;
    }

    private static bool tryPush(List<Avm1Value> stack, Avm1Value value, int maxStackValues)
    {
        if (stack.Count >= maxStackValues)
            return false;
        stack.Add(value);
        return true;
    }

    private static bool toBoolean(Avm1Value value) => value.Kind switch
    {
        Avm1Kind.Undefined or Avm1Kind.Null => false,
        Avm1Kind.Boolean => value.Boolean,
        Avm1Kind.Number => value.Number != 0 && !double.IsNaN(value.Number),
        Avm1Kind.String => value.String!.Length != 0,
        _ => true,
    };

    private static Avm1Value add2(Avm1Value left, Avm1Value right) =>
        left.IsString || right.IsString
            ? toAvmString(left) + toAvmString(right)
            : toNumber(left) + toNumber(right);

    private static bool lessThan(Avm1Value left, Avm1Value right)
    {
        if (left.String is { } leftText && right.String is { } rightText)
            return string.CompareOrdinal(leftText, rightText) < 0;
        var leftNumber = toNumber(left);
        var rightNumber = toNumber(right);
        return !double.IsNaN(leftNumber) && !double.IsNaN(rightNumber) && leftNumber < rightNumber;
    }

    private static bool looseEquals(Avm1Value left, Avm1Value right)
    {
        if (left.IsNullish && right.IsNullish)
            return true;
        if (left.IsBoolean)
            return looseEquals(toNumber(left), right);
        if (right.IsBoolean)
            return looseEquals(left, toNumber(right));
        if (left.IsNumber && right.IsString || left.IsString && right.IsNumber)
            return numericEquals(toNumber(left), toNumber(right));
        return strictEquals(left, right);
    }

    // Objects, functions and clips: the same one (Flash compares them by reference).
    private static bool strictEquals(Avm1Value left, Avm1Value right) => left.Kind == right.Kind && left.Kind switch
    {
        Avm1Kind.Undefined or Avm1Kind.Null => true,
        Avm1Kind.Boolean => left.Boolean == right.Boolean,
        Avm1Kind.Number => numericEquals(left.Number, right.Number),
        Avm1Kind.String => left.String == right.String,
        _ => ReferenceEquals(left.Object, right.Object),
    };

    private static bool numericEquals(double left, double right) =>
        !double.IsNaN(left) && !double.IsNaN(right) && left == right;

    internal static double toNumber(Avm1Value value) => value.Kind switch
    {
        Avm1Kind.Null => 0,
        Avm1Kind.Undefined => double.NaN,
        Avm1Kind.Boolean => value.Boolean ? 1 : 0,
        Avm1Kind.Number => value.Number,
        Avm1Kind.String => parseNumber(value.String!),
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

    internal static string toAvmString(Avm1Value value) => value.Kind switch
    {
        Avm1Kind.Null => "null",
        Avm1Kind.Undefined => "undefined",
        Avm1Kind.Boolean => value.Boolean ? "true" : "false",
        Avm1Kind.Number => numberText(value.Number),
        Avm1Kind.String => value.String!,
        _ => "[object Object]",
    };

    // The whole numbers scripts turn into text most (indices, counts, levels) are made once.
    private static readonly string[] s_wholeTexts = [.. Enumerable.Range(0, 1024).Select(static whole => whole.ToString(CultureInfo.InvariantCulture))];

    private static string numberText(double number) =>
        double.IsNaN(number) ? "NaN"
        : double.IsPositiveInfinity(number) ? "Infinity"
        : double.IsNegativeInfinity(number) ? "-Infinity"
        : number >= 0 && number < 1024 && number == Math.Floor(number) && !(number == 0 && double.IsNegative(number))
            ? s_wholeTexts[(int)number]
        : number.ToString("G15", CultureInfo.InvariantCulture);

    private static bool tryUnary(List<Avm1Value> stack, Func<Avm1Value, Avm1Value> operation)
    {
        if (!tryPop(stack, out var value))
            return false;
        stack.Add(operation(value));
        return true;
    }

    private static string getString(ImmutableArray<LmbString> strings, ushort index) =>
        index < strings.Length ? strings[index].Value : "";

    private static int toInt32(Avm1Value value)
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

    // A value's old object form (Avm1Value.Boxed): true and false have one box each, and so have the whole numbers
    // scripts use most (frames, depths, counters).
    private static readonly object BoxedTrue = true, BoxedFalse = false;
    private static readonly object[] BoxedWholes = [.. Enumerable.Range(-256, 1280).Select(static whole => (object)(double)whole)];

    internal static object Box(bool value) => value ? BoxedTrue : BoxedFalse;

    private static Avm1Value val(double value) => value;

    private static Avm1Value val(bool value) => value;

    internal static object Box(double value) =>
        value >= -256 && value < 1024 && value == Math.Floor(value) && !(value == 0 && double.IsNegative(value))
            ? BoxedWholes[(int)value + 256] : value;

    private static bool tryBinary(List<Avm1Value> stack, Func<Avm1Value, Avm1Value, Avm1Value> operation)
    {
        if (!tryPop(stack, out var right) || !tryPop(stack, out var left))
            return false;
        stack.Add(operation(left, right));
        return true;
    }

    private static bool tryPop(List<Avm1Value> stack, out Avm1Value value)
    {
        if (stack.Count == 0)
        {
            value = default;
            return false;
        }
        value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return true;
    }

    private static bool tryPeek(List<Avm1Value> stack, out Avm1Value value)
    {
        if (stack.Count == 0)
        {
            value = default;
            return false;
        }
        value = stack[^1];
        return true;
    }

    private static bool tryPopArguments(List<Avm1Value> stack, out IReadOnlyList<Avm1Value> arguments)
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
        var values = new Avm1Value[count];
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
