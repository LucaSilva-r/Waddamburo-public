using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Lmb;

public sealed record Avm1CodeBlock(
    int Offset,
    int Length,
    ImmutableArray<Avm1Instruction> Instructions);

public sealed record Avm1Instruction(
    byte Opcode,
    int Offset,
    int Length,
    ImmutableArray<byte> OperandBytes,
    Avm1Operand? Operand,
    int? BranchTarget,
    Avm1CodeBlock? Body);

public abstract record Avm1Operand;

public sealed record Avm1FrameOperand(ushort Frame) : Avm1Operand;

public sealed record Avm1RegisterOperand(byte Register) : Avm1Operand;

public sealed record Avm1StringIndexOperand(ushort StringIndex) : Avm1Operand;

public sealed record Avm1WaitForFrameOperand(ushort Frame, byte SkipCount) : Avm1Operand;

public sealed record Avm1WaitForFrame2Operand(byte SkipCount) : Avm1Operand;

public sealed record Avm1BranchOperand(short Displacement, int Target) : Avm1Operand;

public sealed record Avm1WithOperand(ushort BodyLength) : Avm1Operand;

public sealed record Avm1GetUrlOperand(string Url, string Target) : Avm1Operand;

public sealed record Avm1FlagsOperand(byte Flags) : Avm1Operand;

public sealed record Avm1GotoFrame2Operand(byte Flags, ushort? SceneBias) : Avm1Operand;

public sealed record Avm1FunctionOperand(
    ushort NameStringIndex,
    byte RegisterCount,
    ushort Flags,
    ImmutableArray<Avm1FunctionParameter> Parameters,
    ushort BodyLength) : Avm1Operand;

public sealed record Avm1FunctionParameter(byte? Register, ushort NameStringIndex);

public sealed record Avm1PushOperand(ImmutableArray<Avm1PushValue> Values) : Avm1Operand;

public abstract record Avm1PushValue(byte Type);

public sealed record Avm1PushStringValue(byte Type, ushort StringIndex) : Avm1PushValue(Type);

public sealed record Avm1PushFloatValue(float Value) : Avm1PushValue(1);

public sealed record Avm1PushNullValue() : Avm1PushValue(2);

public sealed record Avm1PushUndefinedValue() : Avm1PushValue(3);

public sealed record Avm1PushRegisterValue(byte Register) : Avm1PushValue(4);

public sealed record Avm1PushBooleanValue(bool Value) : Avm1PushValue(5);

public sealed record Avm1PushDoubleValue(double Value) : Avm1PushValue(6);

public sealed record Avm1PushIntegerValue(int Value) : Avm1PushValue(7);

internal static class Avm1BytecodeReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const byte DefineFunction2 = 0x8E;
    private const byte With = 0x94;
    private const byte Push = 0x96;
    private const byte Jump = 0x99;
    private const byte DefineFunction = 0x9B;
    private const byte If = 0x9D;

    public static Avm1CodeBlock Read(
        ImmutableArray<byte> bytecode,
        long absoluteOffset,
        ParserLimits limits) =>
        readBlock(bytecode.AsSpan(), 0, absoluteOffset, new DecoderState(limits), depth: 0);

    private static Avm1CodeBlock readBlock(
        ReadOnlySpan<byte> bytes,
        int blockOffset,
        long absoluteOffset,
        DecoderState state,
        int depth)
    {
        if (depth > state.Limits.MaxActionNesting)
            throw new FormatLimitException(nameof(ParserLimits.MaxActionNesting), depth, state.Limits.MaxActionNesting, absoluteOffset);
        var instructions = ImmutableArray.CreateBuilder<Avm1Instruction>();
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            var instructionOffset = cursor;
            state.InstructionCount++;
            if (state.InstructionCount > state.Limits.MaxActionInstructions)
                throw new FormatLimitException(nameof(ParserLimits.MaxActionInstructions), state.InstructionCount, state.Limits.MaxActionInstructions, absoluteOffset + cursor);
            var opcode = bytes[cursor++];
            var payloadOffset = cursor;
            var payloadLength = 0;
            if (opcode >= 0x80)
            {
                require(bytes, cursor, 2, absoluteOffset, "AVM action length");
                payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor, 2));
                cursor += 2;
                payloadOffset = cursor;
                require(bytes, cursor, payloadLength, absoluteOffset, "AVM action payload");
                cursor += payloadLength;
            }

            var payload = bytes.Slice(payloadOffset, payloadLength);
            Avm1Operand? operand = null;
            int? branchTarget = null;
            Avm1CodeBlock? body = null;
            var bodyLength = 0;
            var hasBody = false;
            if (opcode is Jump or If)
            {
                requireExactLength(payload, 2, absoluteOffset + payloadOffset, "AVM branch operand");
                var displacement = BinaryPrimitives.ReadInt16LittleEndian(payload);
                branchTarget = checked(blockOffset + cursor + displacement);
                operand = new Avm1BranchOperand(displacement, branchTarget.Value);
            }
            else if (opcode == With)
            {
                requireExactLength(payload, 2, absoluteOffset + payloadOffset, "AVM with header");
                bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                operand = new Avm1WithOperand(checked((ushort)bodyLength));
                hasBody = true;
            }
            else if (opcode is DefineFunction or DefineFunction2)
            {
                var function = readFunctionOperand(opcode, payload, absoluteOffset + payloadOffset);
                operand = function;
                bodyLength = function.BodyLength;
                hasBody = true;
            }
            else
                operand = readOperand(opcode, payload, absoluteOffset + payloadOffset);

            if (hasBody)
            {
                require(bytes, cursor, bodyLength, absoluteOffset, "AVM lexical body");
                body = readBlock(bytes.Slice(cursor, bodyLength), blockOffset + cursor, absoluteOffset + cursor, state, depth + 1);
                cursor += bodyLength;
            }

            instructions.Add(new Avm1Instruction(
                opcode,
                blockOffset + instructionOffset,
                cursor - instructionOffset,
                ImmutableArray.CreateRange(payload.ToArray()),
                operand,
                branchTarget,
                body));
        }

        var boundaries = instructions.Select(instruction => instruction.Offset).ToHashSet();
        boundaries.Add(blockOffset + bytes.Length);
        foreach (var instruction in instructions)
        {
            if (instruction.BranchTarget is int target && !boundaries.Contains(target))
            {
                throw new FormatReadException(
                    "AVM branch target is not an instruction boundary",
                    absoluteOffset + instruction.Offset - blockOffset,
                    instruction.Length,
                    instruction.Length);
            }
        }

        return new Avm1CodeBlock(blockOffset, bytes.Length, instructions.ToImmutable());
    }

    private static Avm1FunctionOperand readFunctionOperand(
        byte opcode,
        ReadOnlySpan<byte> payload,
        long absoluteOffset)
    {
        require(payload, 0, 4, absoluteOffset, "Lumen AVM function header");
        var nameStringIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var cursor = 2; // Lumen stores the function name as an F001 string-pool index.
        var parameterCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        byte registerCount = 0;
        ushort flags = 0;
        var parameters = ImmutableArray.CreateBuilder<Avm1FunctionParameter>(parameterCount);
        if (opcode == DefineFunction2)
        {
            require(payload, cursor, 3, absoluteOffset, "AVM function2 header");
            registerCount = payload[cursor];
            flags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor + 1, 2));
            cursor += 3;
            for (var index = 0; index < parameterCount; index++)
            {
                require(payload, cursor, 3, absoluteOffset, "Lumen AVM function2 parameter");
                parameters.Add(new Avm1FunctionParameter(
                    payload[cursor],
                    BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor + 1, 2))));
                cursor += 3;
            }
        }
        else
        {
            for (var index = 0; index < parameterCount; index++)
            {
                require(payload, cursor, 2, absoluteOffset, "Lumen AVM function parameter");
                parameters.Add(new Avm1FunctionParameter(
                    null,
                    BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2))));
                cursor += 2;
            }
        }

        require(payload, cursor, 2, absoluteOffset, "AVM function body length");
        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        requireExactLength(payload, cursor, absoluteOffset, "Lumen AVM function header");
        return new Avm1FunctionOperand(
            nameStringIndex,
            registerCount,
            flags,
            parameters.MoveToImmutable(),
            bodyLength);
    }

    private static Avm1Operand? readOperand(byte opcode, ReadOnlySpan<byte> payload, long absoluteOffset) =>
        opcode switch
        {
            0x81 => new Avm1FrameOperand(readUInt16(payload, absoluteOffset, "AVM frame")),
            0x83 => readGetUrl(payload, absoluteOffset),
            0x87 => new Avm1RegisterOperand(readByte(payload, absoluteOffset, "AVM register")),
            0x8A => readWaitForFrame(payload, absoluteOffset),
            0x8B or 0x8C => new Avm1StringIndexOperand(readUInt16(payload, absoluteOffset, "AVM string index")),
            0x8D => new Avm1WaitForFrame2Operand(readByte(payload, absoluteOffset, "AVM skip count")),
            Push => readPush(payload, absoluteOffset),
            0x9A => new Avm1FlagsOperand(readByte(payload, absoluteOffset, "AVM URL flags")),
            0x9F => readGotoFrame2(payload, absoluteOffset),
            _ => null,
        };

    private static Avm1GetUrlOperand readGetUrl(ReadOnlySpan<byte> payload, long absoluteOffset)
    {
        var firstEnd = payload.IndexOf((byte)0);
        if (firstEnd < 0)
            throw new FormatReadException("AVM URL is not NUL terminated", absoluteOffset, 1, 0);
        var target = payload.Slice(firstEnd + 1);
        var secondEnd = target.IndexOf((byte)0);
        if (secondEnd < 0 || secondEnd != target.Length - 1)
            throw new FormatReadException("AVM URL target is not exactly NUL terminated", absoluteOffset + firstEnd + 1, target.Length, Math.Max(0, secondEnd));
        try
        {
            return new Avm1GetUrlOperand(
                StrictUtf8.GetString(payload.Slice(0, firstEnd)),
                StrictUtf8.GetString(target.Slice(0, secondEnd)));
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatReadException("AVM URL operands are not valid UTF-8", absoluteOffset, payload.Length, payload.Length, exception);
        }
    }

    private static Avm1WaitForFrameOperand readWaitForFrame(ReadOnlySpan<byte> payload, long absoluteOffset)
    {
        requireExactLength(payload, 3, absoluteOffset, "AVM wait-for-frame operand");
        return new Avm1WaitForFrameOperand(BinaryPrimitives.ReadUInt16LittleEndian(payload), payload[2]);
    }

    private static Avm1GotoFrame2Operand readGotoFrame2(ReadOnlySpan<byte> payload, long absoluteOffset)
    {
        if (payload.Length is not (1 or 3))
            throw new FormatReadException("Invalid AVM goto-frame-2 operand length", absoluteOffset, 1, payload.Length);
        return new Avm1GotoFrame2Operand(
            payload[0],
            payload.Length == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2)) : null);
    }

    private static Avm1PushOperand readPush(ReadOnlySpan<byte> payload, long absoluteOffset)
    {
        var values = ImmutableArray.CreateBuilder<Avm1PushValue>();
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var type = payload[cursor++];
            Avm1PushValue value = type switch
            {
                0 => new Avm1PushStringValue(type, readUInt16(payload, ref cursor, absoluteOffset, "Lumen AVM push string index")),
                1 => new Avm1PushFloatValue(BitConverter.Int32BitsToSingle(readInt32(payload, ref cursor, absoluteOffset, "AVM push float"))),
                2 => new Avm1PushNullValue(),
                3 => new Avm1PushUndefinedValue(),
                4 => new Avm1PushRegisterValue(readByte(payload, ref cursor, absoluteOffset, "AVM push register")),
                5 => new Avm1PushBooleanValue(readByte(payload, ref cursor, absoluteOffset, "AVM push boolean") != 0),
                6 => new Avm1PushDoubleValue(BitConverter.Int64BitsToDouble(readInt64(payload, ref cursor, absoluteOffset, "AVM push double"))),
                7 => new Avm1PushIntegerValue(readInt32(payload, ref cursor, absoluteOffset, "AVM push integer")),
                8 => new Avm1PushStringValue(type, readByte(payload, ref cursor, absoluteOffset, "AVM push string index")),
                9 => new Avm1PushStringValue(type, readUInt16(payload, ref cursor, absoluteOffset, "AVM push string index")),
                _ => throw new FormatReadException("Unknown AVM push value type", absoluteOffset + cursor - 1, 1, 1),
            };
            values.Add(value);
        }
        return new Avm1PushOperand(values.ToImmutable());
    }

    private static byte readByte(ReadOnlySpan<byte> payload, long absoluteOffset, string context)
    {
        requireExactLength(payload, 1, absoluteOffset, context);
        return payload[0];
    }

    private static ushort readUInt16(ReadOnlySpan<byte> payload, long absoluteOffset, string context)
    {
        requireExactLength(payload, 2, absoluteOffset, context);
        return BinaryPrimitives.ReadUInt16LittleEndian(payload);
    }

    private static byte readByte(ReadOnlySpan<byte> payload, ref int cursor, long absoluteOffset, string context)
    {
        require(payload, cursor, 1, absoluteOffset, context);
        return payload[cursor++];
    }

    private static ushort readUInt16(ReadOnlySpan<byte> payload, ref int cursor, long absoluteOffset, string context)
    {
        require(payload, cursor, 2, absoluteOffset, context);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        return value;
    }

    private static int readInt32(ReadOnlySpan<byte> payload, ref int cursor, long absoluteOffset, string context)
    {
        require(payload, cursor, 4, absoluteOffset, context);
        var value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        return value;
    }

    private static long readInt64(ReadOnlySpan<byte> payload, ref int cursor, long absoluteOffset, string context)
    {
        require(payload, cursor, 8, absoluteOffset, context);
        var value = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor, 8));
        cursor += 8;
        return value;
    }

    private static void require(ReadOnlySpan<byte> bytes, int offset, int length, long absoluteOffset, string context)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            var available = Math.Max(0, bytes.Length - Math.Max(0, offset));
            throw new FormatReadException($"Truncated {context}", absoluteOffset + offset, length, available);
        }
    }

    private static void requireExactLength(ReadOnlySpan<byte> bytes, int expected, long absoluteOffset, string context)
    {
        if (bytes.Length != expected)
            throw new FormatReadException($"Invalid {context} length", absoluteOffset, expected, bytes.Length);
    }

    private sealed class DecoderState(ParserLimits limits)
    {
        public ParserLimits Limits { get; } = limits;

        public long InstructionCount { get; set; }
    }
}
