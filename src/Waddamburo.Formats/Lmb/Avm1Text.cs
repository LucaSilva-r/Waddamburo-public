using System.Globalization;

namespace Waddamburo.Formats.Lmb;

/// <summary>
/// A compiled script as text, to read what a movie does (shown by the movie editor for a movie that has no source).
/// Straight-line code comes out as statements; a block whose jumps cannot be followed with an empty stack is listed
/// instruction by instruction instead. A reading aid: it is not source and does not compile back.
/// </summary>
public static class Avm1Text
{
    public static string Decompile(Avm1CodeBlock code, Func<uint, string> text)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        block(code, text, lines, "");
        return string.Join('\n', lines);
    }

    private static void block(Avm1CodeBlock code, Func<uint, string> text, List<string> lines, string indent)
    {
        var mark = lines.Count;
        try
        {
            statements(code, text, lines, indent);
        }
        catch (NotSupportedException)
        {
            lines.RemoveRange(mark, lines.Count - mark);
            lines.Add(indent + "// not rebuilt into statements: its instructions");
            foreach (var instruction in code.Instructions)
                lines.Add($"{indent}// {name(instruction.Opcode)}{operand(instruction, text)}");
        }
    }

    private static string quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static bool identifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] is '_' or '$')
        && value.All(static c => char.IsLetterOrDigit(c) || c is '_' or '$');

    // A value that names something (a quoted identifier) as the bare name; anything else is looked up by expression.
    private static string bare(string expression) =>
        expression.Length > 2 && expression[0] == '"' && identifier(expression[1..^1]) ? expression[1..^1] : $"eval({expression})";

    private static string member(string target, string key) =>
        key.Length > 2 && key[0] == '"' && identifier(key[1..^1]) ? $"{target}.{key[1..^1]}" : $"{target}[{key}]";

    private static string literal(Avm1PushValue value, Func<uint, string> text) => value switch
    {
        Avm1PushStringValue s => quote(text(s.StringIndex)),
        Avm1PushFloatValue f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        Avm1PushDoubleValue d => d.Value.ToString("R", CultureInfo.InvariantCulture),
        Avm1PushIntegerValue i => i.Value.ToString(CultureInfo.InvariantCulture),
        Avm1PushBooleanValue b => b.Value ? "true" : "false",
        Avm1PushNullValue => "null",
        Avm1PushUndefinedValue => "undefined",
        Avm1PushRegisterValue r => $"r{r.Register}",
        _ => throw new NotSupportedException(),
    };

    private static void statements(Avm1CodeBlock code, Func<uint, string> text, List<string> lines, string indent)
    {
        var stack = new List<string>();
        string pop()
        {
            if (stack.Count == 0) throw new NotSupportedException();
            var top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return top;
        }
        string[] arguments()
        {
            if (!int.TryParse(pop(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0 || count > stack.Count)
                throw new NotSupportedException();
            return [.. Enumerable.Range(0, count).Select(_ => pop())];
        }
        void binary(string op)
        {
            var (right, left) = (pop(), pop());
            stack.Add($"({left} {op} {right})");
        }
        // Jumps are followed only between statements: a value left on the stack across one is beyond this reader.
        var targets = code.Instructions.Where(static each => each.Opcode is 0x99 or 0x9D)
            .Select(static each => ((Avm1BranchOperand)each.Operand!).Target).ToHashSet();
        for (var index = 0; index < code.Instructions.Length; index++)
        {
            var instruction = code.Instructions[index];
            if (targets.Contains(instruction.Offset))
            {
                if (stack.Count > 0) throw new NotSupportedException();
                lines.Add($"{indent[..Math.Max(0, indent.Length - 2)]}  L{instruction.Offset}:");
            }
            var followedByPlay = index + 1 < code.Instructions.Length && code.Instructions[index + 1].Opcode == 0x06;
            switch (instruction.Opcode)
            {
                case 0x00: break;
                case 0x06: lines.Add(indent + "play();"); break;
                case 0x07: lines.Add(indent + "stop();"); break;
                case 0x0A or 0x47: binary("+"); break;
                case 0x0B: binary("-"); break;
                case 0x0C: binary("*"); break;
                case 0x0D: binary("/"); break;
                case 0x3F: binary("%"); break;
                case 0x0E or 0x49: binary("=="); break;
                case 0x66: binary("==="); break;
                case 0x0F or 0x48: binary("<"); break;
                case 0x67: binary(">"); break;
                case 0x60: binary("&"); break;
                case 0x61: binary("|"); break;
                case 0x62: binary("^"); break;
                case 0x63: binary("<<"); break;
                case 0x64: binary(">>"); break;
                case 0x65: binary(">>>"); break;
                case 0x12: stack.Add($"!{pop()}"); break;
                case 0x18: stack.Add($"int({pop()})"); break;
                case 0x4A: stack.Add($"Number({pop()})"); break;
                case 0x4B: stack.Add($"String({pop()})"); break;
                case 0x50: stack.Add($"({pop()} + 1)"); break;
                case 0x51: stack.Add($"({pop()} - 1)"); break;
                case 0x17: // Pop: a call made for its effect is a statement; a plain value is dropped
                {
                    var dropped = pop();
                    if (dropped.Contains('(') || dropped.StartsWith("new ", StringComparison.Ordinal)) lines.Add($"{indent}{dropped};");
                    break;
                }
                case 0x26: lines.Add($"{indent}trace({pop()});"); break;
                case 0x1C: stack.Add(bare(pop())); break;
                case 0x1D:
                {
                    var value = pop();
                    lines.Add($"{indent}{bare(pop())} = {value};");
                    break;
                }
                case 0x3C:
                {
                    var value = pop();
                    lines.Add($"{indent}var {bare(pop())} = {value};");
                    break;
                }
                case 0x41: lines.Add($"{indent}var {bare(pop())};"); break;
                case 0x4E:
                {
                    var key = pop();
                    stack.Add(member(pop(), key));
                    break;
                }
                case 0x4F:
                {
                    var (value, key) = (pop(), pop());
                    lines.Add($"{indent}{member(pop(), key)} = {value};");
                    break;
                }
                case 0x3A:
                {
                    var key = pop();
                    lines.Add($"{indent}delete {member(pop(), key)};");
                    stack.Add("true");
                    break;
                }
                case 0x3D:
                {
                    var function = bare(pop());
                    stack.Add($"{function}({string.Join(", ", arguments())})");
                    break;
                }
                case 0x52:
                {
                    var (method, target) = (pop(), pop());
                    var call = method is "undefined" or "\"\"" ? target : member(target, method);
                    stack.Add($"{call}({string.Join(", ", arguments())})");
                    break;
                }
                case 0x40:
                {
                    var type = bare(pop());
                    stack.Add($"new {type}({string.Join(", ", arguments())})");
                    break;
                }
                case 0x53:
                {
                    var (method, target) = (pop(), pop());
                    var type = method is "undefined" or "\"\"" ? target : member(target, method);
                    stack.Add($"new {type}({string.Join(", ", arguments())})");
                    break;
                }
                case 0x3E: lines.Add($"{indent}return {pop()};"); break;
                case 0x42: stack.Add($"[{string.Join(", ", arguments())}]"); break;
                case 0x43:
                {
                    if (!int.TryParse(pop(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) throw new NotSupportedException();
                    var fields = new List<string>();
                    for (var each = 0; each < count; each++)
                    {
                        var value = pop();
                        var key = pop();
                        fields.Insert(0, $"{(key.Length > 2 && identifier(key[1..^1]) ? key[1..^1] : key)}: {value}");
                    }
                    stack.Add($"{{ {string.Join(", ", fields)} }}");
                    break;
                }
                case 0x4C:
                {
                    var top = pop();
                    stack.Add(top);
                    stack.Add(top);
                    break;
                }
                case 0x4D:
                {
                    var (a, b) = (pop(), pop());
                    stack.Add(a);
                    stack.Add(b);
                    break;
                }
                case 0x69:
                {
                    var parent = pop();
                    lines.Add($"{indent}// class {pop()} extends {parent}");
                    break;
                }
                case 0x96:
                    foreach (var value in ((Avm1PushOperand)instruction.Operand!).Values)
                        stack.Add(literal(value, text));
                    break;
                case 0x87: // StoreRegister keeps the value on the stack: named, so it is not written twice
                {
                    var register = $"r{((Avm1RegisterOperand)instruction.Operand!).Register}";
                    lines.Add($"{indent}{register} = {pop()};");
                    stack.Add(register);
                    break;
                }
                case 0x8C:
                    lines.Add($"{indent}{(followedByPlay ? "gotoAndPlay" : "gotoAndStop")}({quote(text(((Avm1StringIndexOperand)instruction.Operand!).StringIndex))});");
                    if (followedByPlay) index++;
                    break;
                case 0x81:
                    lines.Add($"{indent}{(followedByPlay ? "gotoAndPlay" : "gotoAndStop")}({((Avm1FrameOperand)instruction.Operand!).Frame + 1});");
                    if (followedByPlay) index++;
                    break;
                case 0x9F:
                    lines.Add($"{indent}{((((Avm1GotoFrame2Operand)instruction.Operand!).Flags & 1) != 0 ? "gotoAndPlay" : "gotoAndStop")}({pop()});");
                    break;
                case 0x83:
                {
                    var command = (Avm1GetUrlOperand)instruction.Operand!;
                    lines.Add($"{indent}fscommand({quote(command.Url)}, {quote(command.Target)});");
                    break;
                }
                case 0x8E or 0x9B:
                {
                    var definition = (Avm1FunctionOperand)instruction.Operand!;
                    var functionName = text(definition.NameStringIndex);
                    var parameters = string.Join(", ", definition.Parameters.Select(parameter =>
                        text(parameter.NameStringIndex) + (parameter.Register is { } register ? $" /* r{register} */" : "")));
                    var body = new List<string>();
                    if (instruction.Body is { } inner) block(inner, text, body, indent + "    ");
                    var head = $"function {functionName}({parameters}) {{";
                    if (functionName.Length == 0)
                        stack.Add(body.Count == 0 ? head + " }" : $"{head}\n{string.Join('\n', body)}\n{indent}}}");
                    else
                    {
                        lines.Add(indent + head);
                        lines.AddRange(body);
                        lines.Add(indent + "}");
                    }
                    break;
                }
                case 0x99:
                    if (stack.Count > 0) throw new NotSupportedException();
                    lines.Add($"{indent}goto L{((Avm1BranchOperand)instruction.Operand!).Target};");
                    break;
                case 0x9D:
                {
                    var condition = pop();
                    if (stack.Count > 0) throw new NotSupportedException();
                    lines.Add($"{indent}if ({condition}) goto L{((Avm1BranchOperand)instruction.Operand!).Target};");
                    break;
                }
                default: throw new NotSupportedException();
            }
        }
    }

    private static string operand(Avm1Instruction instruction, Func<uint, string> text) => instruction.Operand switch
    {
        Avm1PushOperand push => " " + string.Join(", ", push.Values.Select(value => literal(value, text))),
        Avm1StringIndexOperand label => " " + quote(text(label.StringIndex)),
        Avm1FrameOperand frame => $" {frame.Frame + 1}",
        Avm1RegisterOperand register => $" r{register.Register}",
        Avm1BranchOperand branch => $" -> {branch.Target}",
        Avm1FunctionOperand function => $" {text(function.NameStringIndex)}({string.Join(", ", function.Parameters.Select(parameter => text(parameter.NameStringIndex)))}), {function.BodyLength} bytes",
        _ => "",
    } + (instruction.Opcode is 0x99 or 0x9D or 0x8E or 0x9B || true ? $"    @{instruction.Offset}" : "");

    private static string name(byte opcode) => opcode switch
    {
        0x00 => "End", 0x06 => "Play", 0x07 => "Stop", 0x0A => "Add", 0x0B => "Subtract", 0x0C => "Multiply", 0x0D => "Divide",
        0x0E => "Equals", 0x0F => "Less", 0x10 => "And", 0x11 => "Or", 0x12 => "Not", 0x17 => "Pop", 0x18 => "ToInteger",
        0x1C => "GetVariable", 0x1D => "SetVariable", 0x22 => "GetProperty", 0x23 => "SetProperty", 0x24 => "CloneSprite",
        0x25 => "RemoveSprite", 0x26 => "Trace", 0x3A => "Delete", 0x3C => "DefineLocal", 0x3D => "CallFunction", 0x3E => "Return",
        0x3F => "Modulo", 0x40 => "NewObject", 0x41 => "DefineLocal2", 0x42 => "InitArray", 0x43 => "InitObject", 0x47 => "Add2",
        0x48 => "Less2", 0x49 => "Equals2", 0x4A => "ToNumber", 0x4B => "ToString", 0x4C => "PushDuplicate", 0x4D => "StackSwap",
        0x4E => "GetMember", 0x4F => "SetMember", 0x50 => "Increment", 0x51 => "Decrement", 0x52 => "CallMethod", 0x53 => "NewMethod",
        0x60 => "BitAnd", 0x61 => "BitOr", 0x62 => "BitXor", 0x63 => "BitLShift", 0x64 => "BitRShift", 0x65 => "BitURShift",
        0x66 => "StrictEquals", 0x67 => "Greater", 0x69 => "Extends", 0x81 => "GotoFrame", 0x83 => "GetUrl", 0x87 => "StoreRegister",
        0x8C => "GoToLabel", 0x8E => "DefineFunction2", 0x96 => "Push", 0x99 => "Jump", 0x9B => "DefineFunction", 0x9D => "If",
        0x9F => "GotoFrame2", _ => $"0x{opcode:X2}",
    };
}
