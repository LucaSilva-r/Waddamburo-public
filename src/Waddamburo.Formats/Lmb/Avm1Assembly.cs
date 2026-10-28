using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Waddamburo.Formats.Lmb;

/// <summary>Lossless Lumen AVM1 assembly. Opaque operands remain hexadecimal; lexical blocks and jumps are relocated.</summary>
public static class Avm1Assembly
{
    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x00]="End", [0x04]="NextFrame", [0x05]="PreviousFrame", [0x06]="Play", [0x07]="Stop", [0x08]="ToggleQuality", [0x09]="StopSounds",
        [0x0A]="Add", [0x0B]="Subtract", [0x0C]="Multiply", [0x0D]="Divide", [0x0E]="Equals", [0x0F]="Less", [0x10]="And", [0x11]="Or", [0x12]="Not",
        [0x13]="StringEquals", [0x14]="StringLength", [0x15]="StringExtract", [0x17]="Pop", [0x18]="ToInteger", [0x1C]="GetVariable", [0x1D]="SetVariable",
        [0x20]="SetTarget2", [0x21]="StringAdd", [0x22]="GetProperty", [0x23]="SetProperty", [0x24]="CloneSprite", [0x25]="RemoveSprite", [0x26]="Trace",
        [0x27]="StartDrag", [0x28]="EndDrag", [0x29]="StringLess", [0x2A]="Throw", [0x2B]="CastOp", [0x2C]="ImplementsOp",
        [0x30]="RandomNumber", [0x31]="MbStringLength", [0x32]="CharToAscii", [0x33]="AsciiToChar", [0x34]="GetTime", [0x35]="MbStringExtract", [0x36]="MbCharToAscii", [0x37]="MbAsciiToChar",
        [0x3A]="Delete", [0x3B]="Delete2", [0x3C]="DefineLocal", [0x3D]="CallFunction", [0x3E]="Return", [0x3F]="Modulo", [0x40]="NewObject", [0x41]="DefineLocal2",
        [0x42]="InitArray", [0x43]="InitObject", [0x44]="TypeOf", [0x45]="TargetPath", [0x46]="Enumerate", [0x47]="Add2", [0x48]="Less2", [0x49]="Equals2",
        [0x4A]="ToNumber", [0x4B]="ToString", [0x4C]="PushDuplicate", [0x4D]="StackSwap", [0x4E]="GetMember", [0x4F]="SetMember", [0x50]="Increment", [0x51]="Decrement",
        [0x52]="CallMethod", [0x53]="NewMethod", [0x54]="InstanceOf", [0x55]="Enumerate2", [0x60]="BitAnd", [0x61]="BitOr", [0x62]="BitXor", [0x63]="BitLShift",
        [0x64]="BitRShift", [0x65]="BitURShift", [0x66]="StrictEquals", [0x67]="Greater", [0x68]="StringGreater", [0x69]="Extends",
        [0x81]="GotoFrame", [0x83]="GetURL", [0x87]="StoreRegister", [0x88]="ConstantPool", [0x8A]="WaitForFrame", [0x8B]="SetTarget", [0x8C]="GotoLabel",
        [0x8D]="WaitForFrame2", [0x8E]="DefineFunction2", [0x8F]="Try", [0x94]="With", [0x96]="Push", [0x99]="Jump", [0x9A]="GetURL2", [0x9B]="DefineFunction",
        [0x9D]="If", [0x9E]="Call", [0x9F]="GotoFrame2",
    };
    private static readonly Dictionary<string, byte> Opcodes = Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static string Disassemble(LmbAction action, IReadOnlyList<LmbString>? strings = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var text = new StringBuilder();
        text.AppendLine("; Lumen AVM1. Operands are raw hex; Push also accepts typed values.");
        text.AppendLine("; Branches use labels. Braces delimit function / With bodies. Ctrl+S applies to the project.");
        void block(Avm1CodeBlock code, int depth)
        {
            var indent = new string(' ', depth * 2);
            var targets = code.Instructions.Where(i => i.BranchTarget.HasValue).Select(i => i.BranchTarget!.Value).ToHashSet();
            foreach (var instruction in code.Instructions)
            {
                if (targets.Contains(instruction.Offset)) text.Append(indent).Append('L').Append(instruction.Offset.ToString("X4", CultureInfo.InvariantCulture)).AppendLine(":");
                text.Append(indent).Append(Names.GetValueOrDefault(instruction.Opcode, $"Op{instruction.Opcode:X2}"));
                if (instruction.BranchTarget is int target) text.Append(" L").Append(target.ToString("X4", CultureInfo.InvariantCulture));
                else if (!instruction.OperandBytes.IsEmpty) text.Append(' ').Append(Convert.ToHexString(instruction.OperandBytes.AsSpan()));
                if (instruction.Body is not null) text.Append(" {");
                var references = instruction.Operand switch
                {
                    Avm1PushOperand push => push.Values.OfType<Avm1PushStringValue>().Select(v => (int)v.StringIndex),
                    Avm1StringIndexOperand value => [value.StringIndex],
                    Avm1FunctionOperand function => new[] { (int)function.NameStringIndex }.Concat(function.Parameters.Select(p => (int)p.NameStringIndex)),
                    _ => Enumerable.Empty<int>(),
                };
                var comments = references.Where(i => strings is not null && i < strings.Count).Select(i => $"s{i}=\"{strings![i].Value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\"");
                var comment = string.Join(", ", comments);
                if (comment.Length > 0) text.Append(" ; ").Append(comment);
                text.AppendLine();
                if (instruction.Body is not null) { block(instruction.Body, depth + 1); text.Append(indent).AppendLine("}"); }
            }
            if (targets.Contains(code.Offset + code.Length)) text.Append(indent).Append('L').Append((code.Offset + code.Length).ToString("X4", CultureInfo.InvariantCulture)).AppendLine(":");
        }
        block(action.Code, 0);
        return text.ToString();
    }

    public static byte[] Assemble(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 8 * 1024 * 1024) throw new FormatException("Assembly exceeds the 8 MiB editor limit.");
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var cursor = 0;
        byte[] block(bool nested, int depth)
        {
            if (depth > 64) throw new FormatException("Too many nested script blocks.");
            using var output = new MemoryStream();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var branches = new List<(int Operand, string Label, int Line)>();
            var size = new byte[2];
            var closed = false;
            while (cursor < lines.Length)
            {
                var lineNumber = ++cursor;
                var line = lines[lineNumber - 1].Split(';', 2)[0].Trim();
                if (line.Length == 0) continue;
                if (line == "}")
                {
                    if (!nested) throw new FormatException($"Line {lineNumber}: unexpected closing brace.");
                    closed = true; break;
                }
                if (line.EndsWith(':'))
                {
                    if (!labels.TryAdd(line[..^1], checked((int)output.Position))) throw new FormatException($"Line {lineNumber}: duplicate label.");
                    continue;
                }
                try
                {
                    var hasBody = line.EndsWith('{');
                    if (hasBody) line = line[..^1].TrimEnd();
                    var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    var name = parts[0]; var operand = parts.Length == 2 ? parts[1].Trim() : "";
                    var opcode = Opcodes.TryGetValue(name, out var value) ? value
                        : name.Length == 4 && name.StartsWith("Op", StringComparison.OrdinalIgnoreCase) ? byte.Parse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : throw new FormatException($"Unknown instruction '{name}'.");
                    var lexical = opcode is 0x8E or 0x94 or 0x9B;
                    if (lexical != hasBody) throw new FormatException("Function and With instructions require a braced body.");
                    byte[] payload;
                    var branch = opcode is 0x99 or 0x9D;
                    if (branch) payload = new byte[2];
                    else if (opcode == 0x96 && (operand.Contains(':') || operand is "null" or "undefined")) payload = push(operand);
                    else payload = Convert.FromHexString(string.Concat(operand.Where(c => !char.IsWhiteSpace(c))));
                    if (opcode < 0x80 && payload.Length > 0) throw new FormatException("This instruction has no operand.");
                    var body = hasBody ? block(true, depth + 1) : [];
                    if (hasBody)
                    {
                        if (payload.Length < 2) throw new FormatException("The lexical header must include its two-byte body length.");
                        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 2), checked((ushort)body.Length));
                    }
                    output.WriteByte(opcode);
                    if (opcode >= 0x80)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(size, checked((ushort)payload.Length)); output.Write(size);
                    }
                    if (branch) branches.Add((checked((int)output.Position), operand, lineNumber));
                    output.Write(payload); output.Write(body);
                }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                { throw new FormatException($"Line {lineNumber}: {error.Message}", error); }
            }
            if (nested && !closed) throw new FormatException("Missing closing brace.");
            var bytes = output.ToArray();
            foreach (var branch in branches)
            {
                if (!labels.TryGetValue(branch.Label, out var target)) throw new FormatException($"Line {branch.Line}: unknown label '{branch.Label}'.");
                var distance = target - branch.Operand - 2;
                if (distance is < short.MinValue or > short.MaxValue) throw new FormatException($"Line {branch.Line}: branch exceeds signed 16-bit range.");
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(branch.Operand), (short)distance);
            }
            return bytes;
        }
        var result = block(false, 0);
        try { _ = Avm1BytecodeReader.Read([.. result], 0, ParserLimits.Default); }
        catch (FormatException error) { throw new FormatException("Invalid AVM1 operands: " + error.Message, error); }
        return result;
    }

    private static byte[] push(string operand)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var token in operand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':', 2); var value = parts.Length > 1 ? parts[1] : "";
            switch (parts[0].ToLowerInvariant())
            {
                case "string": writer.Write((byte)0); writer.Write(ushort.Parse(value, CultureInfo.InvariantCulture)); break;
                case "string8": writer.Write((byte)8); writer.Write(byte.Parse(value, CultureInfo.InvariantCulture)); break;
                case "string16": writer.Write((byte)9); writer.Write(ushort.Parse(value, CultureInfo.InvariantCulture)); break;
                case "int": writer.Write((byte)7); writer.Write(int.Parse(value, CultureInfo.InvariantCulture)); break;
                case "float": writer.Write((byte)1); writer.Write(float.Parse(value, CultureInfo.InvariantCulture)); break;
                case "double": writer.Write((byte)6); writer.Write(double.Parse(value, CultureInfo.InvariantCulture)); break;
                case "register": writer.Write((byte)4); writer.Write(byte.Parse(value, CultureInfo.InvariantCulture)); break;
                case "bool": writer.Write((byte)5); writer.Write(bool.Parse(value)); break;
                case "null": writer.Write((byte)2); break;
                case "undefined": writer.Write((byte)3); break;
                default: throw new FormatException($"Unknown Push type '{parts[0]}'.");
            }
        }
        return stream.ToArray();
    }
}
