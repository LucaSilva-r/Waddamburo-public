using System.Buffers.Binary;
using System.Collections.Immutable;
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
    int? BranchTarget,
    Avm1CodeBlock? Body);

internal static class Avm1BytecodeReader
{
    private const byte DefineFunction2 = 0x8E;
    private const byte With = 0x94;
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
            int? branchTarget = null;
            Avm1CodeBlock? body = null;
            var bodyLength = 0;
            var hasBody = false;
            if (opcode is Jump or If)
            {
                requireExactLength(payload, 2, absoluteOffset + payloadOffset, "AVM branch operand");
                branchTarget = checked(blockOffset + cursor + BinaryPrimitives.ReadInt16LittleEndian(payload));
            }
            else if (opcode == With)
            {
                requireExactLength(payload, 2, absoluteOffset + payloadOffset, "AVM with header");
                bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                hasBody = true;
            }
            else if (opcode is DefineFunction or DefineFunction2)
            {
                bodyLength = readFunctionBodyLength(opcode, payload, absoluteOffset + payloadOffset);
                hasBody = true;
            }

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

    private static int readFunctionBodyLength(
        byte opcode,
        ReadOnlySpan<byte> payload,
        long absoluteOffset)
    {
        require(payload, 0, 4, absoluteOffset, "Lumen AVM function header");
        var cursor = 2; // Lumen stores the function name as an F001 string-pool index.
        var parameterCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        if (opcode == DefineFunction2)
        {
            require(payload, cursor, 3, absoluteOffset, "AVM function2 header");
            cursor += 3;
            for (var index = 0; index < parameterCount; index++)
            {
                require(payload, cursor, 3, absoluteOffset, "Lumen AVM function2 parameter");
                cursor += 3; // register byte plus F001 string-pool index
            }
        }
        else
        {
            for (var index = 0; index < parameterCount; index++)
            {
                require(payload, cursor, 2, absoluteOffset, "Lumen AVM function parameter");
                cursor += 2; // F001 string-pool index
            }
        }

        require(payload, cursor, 2, absoluteOffset, "AVM function body length");
        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        requireExactLength(payload, cursor, absoluteOffset, "Lumen AVM function header");
        return bodyLength;
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
