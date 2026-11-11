using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Waddamburo.Formats.Package;

/// <summary>A script error, with its place in the source: <c>file(line,column): message</c>.</summary>
public sealed class ActionScriptException(string file, int line, int column, string message)
    : FormatException($"{file}({line},{column}): {message}")
{
    public int Line { get; } = line;

    public int Column { get; } = column;
}

/// <summary>
/// Compiles ActionScript 2 source to the AVM1 code the Lumen interpreter runs (strings as indices into the
/// movie's pool), for the scripts of movie packages (docs/development/free-movies-plan.md).
/// The language is what the interpreter can run: variables, functions and closures, objects and arrays,
/// classes (<c>extends</c>, <c>super</c>, <c>static</c>), <c>if</c>, <c>while</c>, <c>do</c>, <c>for</c>,
/// <c>switch</c>, the usual operators; type annotations are read and ignored. <c>for..in</c>, <c>try</c>,
/// <c>throw</c>, <c>with</c>, <c>typeof</c> and <c>instanceof</c> have no opcode in the interpreter yet and are
/// reported as errors.
/// ponytail: no optimisation at all (locals by name, one push per value); registers and merged pushes if
/// a script ever shows up in a profile.
/// </summary>
public static class ActionScript
{
    /// <param name="source">The script.</param>
    /// <param name="file">Its name, for error messages.</param>
    /// <param name="intern">Gives a string's index in the movie's string pool, adding it when new.</param>
    /// <returns>One action record: the script's code, ended by End.</returns>
    public static byte[] Compile(string source, string file, Func<string, ushort> intern)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(intern);
        var parser = new Parser(new Lexer(source, file).Tokens, file);
        var program = parser.Program();
        return new Generator(file, intern, parser.Imports).Script(program);
    }

    // ---------------------------------------------------------------------------------------------- lexer

    private enum TokenKind { Name, Number, Text, Symbol, End }

    private readonly record struct Token(TokenKind Kind, string Value, double Number, int Line, int Column);

    private sealed class Lexer
    {
        private static readonly string[] Symbols =
        [
            ">>>=", "===", "!==", ">>>", "<<=", ">>=", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=",
            "/=", "%=", "&=", "|=", "^=", "<<", ">>", "{", "}", "(", ")", "[", "]", ";", ",", ".", "?", ":", "=", "+", "-",
            "*", "/", "%", "<", ">", "!", "~", "&", "|", "^",
        ];

        public List<Token> Tokens { get; } = [];

        public Lexer(string source, string file)
        {
            int at = 0, line = 1, lineStart = 0;
            ActionScriptException error(string message) => new(file, line, at - lineStart + 1, message);
            while (true)
            {
                while (at < source.Length)
                {
                    if (source[at] == '\n')
                        (line, lineStart) = (line + 1, at + 1);
                    if (char.IsWhiteSpace(source[at]))
                        at++;
                    else if (string.CompareOrdinal(source, at, "//", 0, 2) == 0)
                        at = source.IndexOf('\n', at) is var end and >= 0 ? end : source.Length;
                    else if (string.CompareOrdinal(source, at, "/*", 0, 2) == 0)
                    {
                        var end = source.IndexOf("*/", at + 2, StringComparison.Ordinal);
                        if (end < 0)
                            throw error("This comment is never closed.");
                        for (; at < end + 2; at++)
                            if (source[at] == '\n')
                                (line, lineStart) = (line + 1, at + 1);
                    }
                    else
                        break;
                }
                var column = at - lineStart + 1;
                if (at >= source.Length)
                {
                    Tokens.Add(new Token(TokenKind.End, "end of file", 0, line, column));
                    return;
                }
                var c = source[at];
                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var start = at;
                    while (at < source.Length && (char.IsLetterOrDigit(source[at]) || source[at] is '_' or '$'))
                        at++;
                    Tokens.Add(new Token(TokenKind.Name, source[start..at], 0, line, column));
                }
                else if (char.IsDigit(c) || (c == '.' && at + 1 < source.Length && char.IsDigit(source[at + 1])))
                {
                    var start = at;
                    double value;
                    if (c == '0' && at + 1 < source.Length && source[at + 1] is 'x' or 'X')
                    {
                        at += 2;
                        while (at < source.Length && Uri.IsHexDigit(source[at]))
                            at++;
                        if (at == start + 2)
                            throw error("A hexadecimal number needs digits.");
                        value = ulong.Parse(source.AsSpan(start + 2, at - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        while (at < source.Length && (char.IsDigit(source[at]) || source[at] == '.'))
                            at++;
                        if (at < source.Length && source[at] is 'e' or 'E')
                        {
                            at++;
                            if (at < source.Length && source[at] is '+' or '-')
                                at++;
                            while (at < source.Length && char.IsDigit(source[at]))
                                at++;
                        }
                        if (!double.TryParse(source.AsSpan(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                            throw error($"'{source[start..at]}' is not a number.");
                    }
                    Tokens.Add(new Token(TokenKind.Number, source[start..at], value, line, column));
                }
                else if (c is '"' or '\'')
                {
                    var text = new StringBuilder();
                    for (at++; ; at++)
                    {
                        if (at >= source.Length || source[at] == '\n')
                            throw error("This string is never closed.");
                        if (source[at] == c)
                            break;
                        if (source[at] != '\\')
                        {
                            text.Append(source[at]);
                            continue;
                        }
                        if (++at >= source.Length)
                            throw error("This string is never closed.");
                        var hex = source[at] == 'x' ? 2 : source[at] == 'u' ? 4 : 0;
                        if (hex > 0)
                        {
                            if (at + hex >= source.Length
                                || !int.TryParse(source.AsSpan(at + 1, hex), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw error("A \\x or \\u escape needs 2 or 4 hexadecimal digits.");
                            text.Append((char)code);
                            at += hex;
                        }
                        else
                            text.Append(source[at] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var other => other });
                    }
                    at++;
                    Tokens.Add(new Token(TokenKind.Text, text.ToString(), 0, line, column));
                }
                else
                {
                    var symbol = Array.Find(Symbols, each => string.CompareOrdinal(source, at, each, 0, each.Length) == 0)
                        ?? throw error($"Unexpected character '{c}'.");
                    at += symbol.Length;
                    Tokens.Add(new Token(TokenKind.Symbol, symbol, 0, line, column));
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ tree

    private abstract record Node(Token At);

    private sealed record Number(Token At, double Value) : Node(At);
    private sealed record Text(Token At, string Value) : Node(At);
    private sealed record Constant(Token At, string Name) : Node(At); // true, false, null, undefined
    private sealed record Name(Token At, string Value) : Node(At);
    private sealed record Member(Token At, Node Target, Node Key) : Node(At);
    private sealed record Call(Token At, Node Callee, List<Node> Arguments, bool Construct) : Node(At);
    private sealed record Unary(Token At, string Operator, Node Operand) : Node(At);
    private sealed record Step(Token At, bool Increment, bool Prefix, Node Target) : Node(At); // ++ and --
    private sealed record Binary(Token At, string Operator, Node Left, Node Right) : Node(At);
    private sealed record Conditional(Token At, Node Test, Node Then, Node Else) : Node(At);
    private sealed record Assign(Token At, string Operator, Node Target, Node Value) : Node(At);
    private sealed record Function(Token At, string? Name, List<string> Parameters, List<Node> Body) : Node(At);
    private sealed record ArrayOf(Token At, List<Node> Items) : Node(At);
    private sealed record ObjectOf(Token At, List<(string Key, Node Value)> Items) : Node(At);
    private sealed record Sequence(Token At, List<Node> Items) : Node(At); // a, b (for headers)

    private sealed record Var(Token At, List<(string Name, Node? Value)> Items) : Node(At);
    private sealed record Evaluate(Token At, Node Expression) : Node(At);
    private sealed record If(Token At, Node Test, Node Then, Node? Else) : Node(At);
    private sealed record Loop(Token At, Node? Before, Node? Test, Node? After, Node Body, bool TestFirst) : Node(At);
    private sealed record Jump(Token At, bool Break) : Node(At);
    private sealed record Return(Token At, Node? Value) : Node(At);
    private sealed record Block(Token At, List<Node> Body) : Node(At);
    private sealed record Switch(Token At, Node Value, List<(Node? Test, List<Node> Body)> Cases) : Node(At);
    private sealed record Class(Token At, string Name, Node? Super, Function? Constructor,
        List<(string Name, Node Value, bool Static)> Members) : Node(At);

    // ---------------------------------------------------------------------------------------------- parser

    private sealed class Parser(List<Token> tokens, string file)
    {
        private static readonly string[] Unsupported = ["try", "throw", "with", "typeof", "instanceof", "interface"];
        private static readonly string[][] Levels =
        [
            ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!=", "===", "!=="], ["<", ">", "<=", ">="], ["<<", ">>", ">>>"],
            ["+", "-"], ["*", "/", "%"],
        ];
        private static readonly string[] Assignments = ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>="];

        private int _at;

        public Dictionary<string, List<string>> Imports { get; } = new(StringComparer.Ordinal);

        private Token Peek => tokens[_at];

        private ActionScriptException error(Token token, string message) => new(file, token.Line, token.Column, message);

        private bool isSymbol(string value) => Peek.Kind == TokenKind.Symbol && Peek.Value == value;

        private bool isWord(string value) => Peek.Kind == TokenKind.Name && Peek.Value == value;

        private bool take(string symbol)
        {
            if (!isSymbol(symbol))
                return false;
            _at++;
            return true;
        }

        private bool takeWord(string word)
        {
            if (!isWord(word))
                return false;
            _at++;
            return true;
        }

        private Token expect(string symbol) =>
            isSymbol(symbol) ? tokens[_at++] : throw error(Peek, $"Expected '{symbol}' but found '{Peek.Value}'.");

        private string name(string what)
        {
            if (Peek.Kind != TokenKind.Name)
                throw error(Peek, $"Expected {what} but found '{Peek.Value}'.");
            return tokens[_at++].Value;
        }

        // ": Type" after a variable, parameter or function header: read and ignored.
        private void skipType()
        {
            if (!take(":"))
                return;
            name("a type");
            while (take("."))
                name("a type");
        }

        public List<Node> Program()
        {
            var body = new List<Node>();
            while (Peek.Kind != TokenKind.End)
                body.Add(statement());
            return body;
        }

        private List<Node> block()
        {
            expect("{");
            var body = new List<Node>();
            while (!take("}"))
            {
                if (Peek.Kind == TokenKind.End)
                    throw error(Peek, "A '{' is never closed.");
                body.Add(statement());
            }
            return body;
        }

        private Node statement()
        {
            var at = Peek;
            if (at.Kind == TokenKind.Name && Array.IndexOf(Unsupported, at.Value) >= 0)
                throw error(at, $"'{at.Value}' is not supported yet.");
            if (isSymbol("{"))
                return new Block(at, block());
            if (take(";"))
                return new Block(at, []);
            if (takeWord("import"))
            {
                // import a.b.C: C alone then means a.b.C. (import a.b.* names nothing by itself.)
                var path = new List<string> { name("a package name") };
                while (take("."))
                {
                    if (take("*"))
                    {
                        path.Clear();
                        break;
                    }
                    path.Add(name("a name"));
                }
                take(";");
                if (path.Count > 1)
                    Imports[path[^1]] = path;
                return new Block(at, []);
            }
            if (takeWord("var"))
            {
                var result = variables(at);
                take(";");
                return result;
            }
            if (isWord("function") && tokens[_at + 1].Kind == TokenKind.Name)
            {
                _at++;
                return function(at, name("a function name"));
            }
            if (isWord("class") || isWord("dynamic") || isWord("intrinsic"))
                return classDeclaration(at);
            if (takeWord("if"))
            {
                expect("(");
                var test = expression();
                expect(")");
                var then = statement();
                return new If(at, test, then, takeWord("else") ? statement() : null);
            }
            if (takeWord("while"))
            {
                expect("(");
                var test = expression();
                expect(")");
                return new Loop(at, null, test, null, statement(), TestFirst: true);
            }
            if (takeWord("do"))
            {
                var body = statement();
                if (!takeWord("while"))
                    throw error(Peek, "Expected 'while' after the body of 'do'.");
                expect("(");
                var test = expression();
                expect(")");
                take(";");
                return new Loop(at, null, test, null, body, TestFirst: false);
            }
            if (takeWord("for"))
            {
                expect("(");
                if (takeWord("each"))
                    throw error(at, "'for each' is not supported yet.");
                Node? before = null;
                if (!isSymbol(";"))
                {
                    var start = Peek;
                    before = takeWord("var") ? variables(start) : new Evaluate(start, sequence());
                    if (isWord("in"))
                        throw error(Peek, "'for..in' is not supported yet.");
                }
                expect(";");
                var test = isSymbol(";") ? null : expression();
                expect(";");
                var after = isSymbol(")") ? null : new Evaluate(Peek, sequence());
                expect(")");
                return new Loop(at, before, test, after, statement(), TestFirst: true);
            }
            if (takeWord("break") || takeWord("continue"))
            {
                take(";");
                return new Jump(at, at.Value == "break");
            }
            if (takeWord("return"))
            {
                var value = isSymbol(";") || isSymbol("}") || Peek.Kind == TokenKind.End ? null : expression();
                take(";");
                return new Return(at, value);
            }
            if (takeWord("switch"))
            {
                expect("(");
                var value = expression();
                expect(")");
                expect("{");
                var cases = new List<(Node?, List<Node>)>();
                while (!take("}"))
                {
                    Node? test = null;
                    if (takeWord("case"))
                        test = expression();
                    else if (!takeWord("default"))
                        throw error(Peek, $"Expected 'case' or 'default' but found '{Peek.Value}'.");
                    expect(":");
                    var body = new List<Node>();
                    while (!isWord("case") && !isWord("default") && !isSymbol("}"))
                        body.Add(statement());
                    cases.Add((test, body));
                }
                return new Switch(at, value, cases);
            }
            var expressionStatement = new Evaluate(at, sequence());
            take(";");
            return expressionStatement;
        }

        private Var variables(Token at)
        {
            var items = new List<(string, Node?)>();
            do
            {
                var variable = name("a variable name");
                skipType();
                items.Add((variable, take("=") ? assignment() : null));
            }
            while (take(","));
            return new Var(at, items);
        }

        private Function function(Token at, string? functionName)
        {
            expect("(");
            var parameters = new List<string>();
            while (!take(")"))
            {
                parameters.Add(name("a parameter name"));
                skipType();
                if (!isSymbol(")"))
                    expect(",");
            }
            skipType();
            return new Function(at, functionName, parameters, block());
        }

        private Class classDeclaration(Token at)
        {
            while (takeWord("dynamic") || takeWord("intrinsic")) { }
            if (!takeWord("class"))
                throw error(Peek, "Expected 'class'.");
            var className = name("a class name");
            while (take("."))
                className = name("a class name"); // A package path: the class is known by its last name.
            Node? super = null;
            if (takeWord("extends"))
            {
                super = new Name(Peek, name("a class name"));
                while (take("."))
                    super = new Member(Peek, super, new Text(Peek, name("a class name")));
            }
            if (isWord("implements"))
                throw error(Peek, "'implements' is not supported yet.");
            expect("{");
            Function? constructor = null;
            var members = new List<(string, Node, bool)>();
            while (!take("}"))
            {
                var isStatic = false;
                while (isWord("public") || isWord("private") || isWord("static"))
                    isStatic |= tokens[_at++].Value == "static";
                var start = Peek;
                if (takeWord("function"))
                {
                    if (isWord("get") || isWord("set"))
                    {
                        if (tokens[_at + 1].Kind == TokenKind.Name)
                            throw error(Peek, "Getters and setters are not supported yet.");
                    }
                    var methodName = name("a method name");
                    var method = function(start, null);
                    if (methodName == className)
                        constructor = method;
                    else
                        members.Add((methodName, method, isStatic));
                }
                else if (takeWord("var"))
                {
                    var variable = name("a member name");
                    skipType();
                    if (take("="))
                        members.Add((variable, assignment(), isStatic));
                    else
                        members.Add((variable, new Constant(start, "undefined"), isStatic));
                    take(";");
                }
                else
                    throw error(Peek, $"Expected 'var' or 'function' in the class but found '{Peek.Value}'.");
            }
            return new Class(at, className, super, constructor, members);
        }

        private Node sequence()
        {
            var at = Peek;
            var first = expression();
            if (!isSymbol(","))
                return first;
            var items = new List<Node> { first };
            while (take(","))
                items.Add(expression());
            return new Sequence(at, items);
        }

        private Node expression() => assignment();

        private Node assignment()
        {
            var target = conditional();
            var at = Peek;
            if (at.Kind != TokenKind.Symbol || Array.IndexOf(Assignments, at.Value) < 0)
                return target;
            if (target is not (Name or Member))
                throw error(at, "Only a variable or a member can be assigned to.");
            _at++;
            return new Assign(at, at.Value, target, assignment());
        }

        private Node conditional()
        {
            var test = binary(0);
            var at = Peek;
            if (!take("?"))
                return test;
            var then = assignment();
            expect(":");
            return new Conditional(at, test, then, assignment());
        }

        private Node binary(int level)
        {
            if (level == Levels.Length)
                return unary();
            var left = binary(level + 1);
            while (Peek.Kind == TokenKind.Symbol && Array.IndexOf(Levels[level], Peek.Value) >= 0)
            {
                var at = tokens[_at++];
                left = new Binary(at, at.Value, left, binary(level + 1));
            }
            if (isWord("instanceof"))
                throw error(Peek, "'instanceof' is not supported yet.");
            return left;
        }

        private Node unary()
        {
            var at = Peek;
            if (take("!") || take("-") || take("+") || take("~"))
                return new Unary(at, at.Value, unary());
            if (take("++") || take("--"))
            {
                var target = unary();
                return target is Name or Member ? new Step(at, at.Value == "++", Prefix: true, target)
                    : throw error(at, $"'{at.Value}' needs a variable or a member.");
            }
            if (takeWord("delete"))
            {
                var target = unary();
                return target is Member ? new Unary(at, "delete", target) : throw error(at, "'delete' needs a member (object.name).");
            }
            if (isWord("typeof"))
                throw error(at, "'typeof' is not supported yet.");
            var value = postfix();
            at = Peek;
            if (take("++") || take("--"))
                return value is Name or Member ? new Step(at, at.Value == "++", Prefix: false, value)
                    : throw error(at, $"'{at.Value}' needs a variable or a member.");
            return value;
        }

        private List<Node> arguments()
        {
            var list = new List<Node>();
            while (!take(")"))
            {
                list.Add(assignment());
                if (!isSymbol(")"))
                    expect(",");
            }
            return list;
        }

        private Node postfix()
        {
            Node value;
            var at = Peek;
            if (takeWord("new"))
            {
                // new a.b.C(args): the callee is a member path, the first "(" starts the arguments.
                Node callee = new Name(Peek, name("a class name"));
                while (take("."))
                    callee = new Member(Peek, callee, new Text(Peek, name("a class name")));
                value = new Call(at, callee, take("(") ? arguments() : [], Construct: true);
            }
            else
                value = primary();
            while (true)
            {
                at = Peek;
                if (take("."))
                    value = new Member(at, value, new Text(at, name("a member name")));
                else if (take("["))
                {
                    value = new Member(at, value, expression());
                    expect("]");
                }
                else if (take("("))
                    value = new Call(at, value, arguments(), Construct: false);
                else
                    return value;
            }
        }

        private Node primary()
        {
            var at = tokens[_at++];
            switch (at.Kind)
            {
                case TokenKind.Number:
                    return new Number(at, at.Number);
                case TokenKind.Text:
                    return new Text(at, at.Value);
                case TokenKind.Name when at.Value is "true" or "false" or "null" or "undefined":
                    return new Constant(at, at.Value);
                case TokenKind.Name when at.Value == "function":
                    return function(at, Peek.Kind == TokenKind.Name ? name("a function name") : null) with { Name = null };
                case TokenKind.Name when Array.IndexOf(Unsupported, at.Value) >= 0:
                    throw error(at, $"'{at.Value}' is not supported yet.");
                case TokenKind.Name:
                    return new Name(at, at.Value);
                case TokenKind.Symbol when at.Value == "(":
                    {
                        var inner = sequence();
                        expect(")");
                        return inner;
                    }
                case TokenKind.Symbol when at.Value == "[":
                    {
                        var items = new List<Node>();
                        while (!take("]"))
                        {
                            items.Add(assignment());
                            if (!isSymbol("]"))
                                expect(",");
                        }
                        return new ArrayOf(at, items);
                    }
                case TokenKind.Symbol when at.Value == "{":
                    {
                        var items = new List<(string, Node)>();
                        while (!take("}"))
                        {
                            var key = Peek.Kind is TokenKind.Text or TokenKind.Number ? tokens[_at++].Value : name("a property name");
                            expect(":");
                            items.Add((key, assignment()));
                            if (!isSymbol("}"))
                                expect(",");
                        }
                        return new ObjectOf(at, items);
                    }
                default:
                    throw error(at, $"Unexpected '{at.Value}'.");
            }
        }
    }

    // ------------------------------------------------------------------------------------------- generator

    private sealed class Generator(string file, Func<string, ushort> intern, Dictionary<string, List<string>> imports)
    {
        private const byte End = 0x00, Play = 0x06, Stop = 0x07, GotoFrame2 = 0x9F, Subtract = 0x0B, Multiply = 0x0C, Divide = 0x0D, Not = 0x12, Pop = 0x17, GetVariable = 0x1C,
            SetVariable = 0x1D, Trace = 0x26, Delete = 0x3A, DefineLocal = 0x3C, CallFunction = 0x3D, ReturnValue = 0x3E,
            Modulo = 0x3F, NewObject = 0x40, DefineLocal2 = 0x41, InitArray = 0x42, InitObject = 0x43, Add = 0x47, Less = 0x48,
            EqualsLoose = 0x49, ToNumber = 0x4A, Duplicate = 0x4C, GetMember = 0x4E, SetMember = 0x4F, Increment = 0x50,
            Decrement = 0x51, CallMethod = 0x52, NewMethod = 0x53, BitAnd = 0x60, BitOr = 0x61, BitXor = 0x62, ShiftLeft = 0x63,
            ShiftRight = 0x64, ShiftRightUnsigned = 0x65, StrictEquals = 0x66, Greater = 0x67, Extends = 0x69,
            DefineFunction2 = 0x8E, PushValues = 0x96, JumpAlways = 0x99, JumpIf = 0x9D;

        // The code of one function body (or the script): branches are patched when it is finished.
        private sealed class Code
        {
            public List<byte> Bytes { get; } = [];
            public List<int> Labels { get; } = [];
            public List<(int At, int Label, Token Token)> Branches { get; } = [];
            public HashSet<string> Locals { get; } = new(StringComparer.Ordinal);
            public Stack<(int Break, int Continue)> Exits { get; } = new();
            public int Switches, Calls;
            public bool IsFunction;
        }

        private readonly Stack<Code> _codes = new();
        private Class? _class;

        private Code Now => _codes.Peek();

        private ActionScriptException error(Node node, string message) => new(file, node.At.Line, node.At.Column, message);

        public byte[] Script(List<Node> program)
        {
            _codes.Push(new Code());
            body(program);
            return finish(program.Count > 0 ? program[0].At : default);
        }

        private byte[] finish(Token at)
        {
            var code = _codes.Pop();
            code.Bytes.Add(End);
            foreach (var (position, label, token) in code.Branches)
            {
                var distance = code.Labels[label] - (position + 2);
                if (distance is < short.MinValue or > short.MaxValue)
                    throw new ActionScriptException(file, token.Line, token.Column, "This block is too long to jump over (split it into functions).");
                BinaryPrimitives.WriteInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(code.Bytes).Slice(position, 2), (short)distance);
            }
            if (code.Bytes.Count > ushort.MaxValue)
                throw new ActionScriptException(file, at.Line, at.Column, "This function is too long (65535 bytes of code at most).");
            return [.. code.Bytes];
        }

        // ---- emit helpers

        private void op(byte code) => Now.Bytes.Add(code);

        private void push(byte type, ReadOnlySpan<byte> value)
        {
            Now.Bytes.Add(PushValues);
            Now.Bytes.Add((byte)(value.Length + 1));
            Now.Bytes.Add(0);
            Now.Bytes.Add(type);
            Now.Bytes.AddRange(value);
        }

        private void pushText(string value)
        {
            Span<byte> index = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(index, intern(value));
            push(0, index);
        }

        private void pushNumber(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue && !(value == 0 && double.IsNegative(value)))
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value);
                push(7, bytes[..4]);
            }
            else
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
                push(6, bytes);
            }
        }

        private void pushConstant(string name)
        {
            if (name is "true" or "false")
                push(5, [(byte)(name == "true" ? 1 : 0)]);
            else
                push((byte)(name == "null" ? 2 : 3), []);
        }

        private int label()
        {
            Now.Labels.Add(-1);
            return Now.Labels.Count - 1;
        }

        private void mark(int label) => Now.Labels[label] = Now.Bytes.Count;

        private void jump(byte code, int label, Node from)
        {
            Now.Bytes.AddRange([code, 2, 0]);
            Now.Branches.Add((Now.Bytes.Count, label, from.At));
            Now.Bytes.AddRange([0, 0]);
        }

        // ---- names

        // A bare name in a method that is one of the class's own members means this.name (or Class.name).
        // An imported name means its full path.
        private Node resolve(Name name)
        {
            if (_codes.Any(code => code.Locals.Contains(name.Value)))
                return name;
            foreach (var (member, _, isStatic) in _class?.Members ?? [])
                if (member == name.Value)
                    return new Member(name.At, new Name(name.At, isStatic ? _class!.Name : "this"), new Text(name.At, member));
            if (!imports.TryGetValue(name.Value, out var path))
                return name;
            Node full = new Name(name.At, path[0]);
            foreach (var part in path.Skip(1))
                full = new Member(name.At, full, new Text(name.At, part));
            return full;
        }

        private static void locals(Node node, HashSet<string> into)
        {
            switch (node)
            {
                case Var variables:
                    foreach (var (name, _) in variables.Items)
                        into.Add(name);
                    break;
                case Function { Name: { } name }:
                    into.Add(name);
                    break;
                case Block block:
                    block.Body.ForEach(each => locals(each, into));
                    break;
                case If branch:
                    locals(branch.Then, into);
                    if (branch.Else is not null)
                        locals(branch.Else, into);
                    break;
                case Loop loop:
                    if (loop.Before is not null)
                        locals(loop.Before, into);
                    locals(loop.Body, into);
                    break;
                case Switch choice:
                    choice.Cases.ForEach(each => each.Body.ForEach(inner => locals(inner, into)));
                    break;
            }
        }

        // ---- statements

        private void body(List<Node> statements)
        {
            foreach (var each in statements)
                locals(each, Now.Locals);
            // Functions exist before the code around them runs, as in Flash.
            foreach (var each in statements.OfType<Function>())
                declare(each);
            foreach (var each in statements.Where(static each => each is not Function))
                statement(each);
        }

        private void declare(Function function)
        {
            if (Now.IsFunction)
            {
                pushText(function.Name!);
                this.function(function with { Name = null });
                op(DefineLocal);
            }
            else
                this.function(function); // On a timeline, a named function is a variable of that timeline.
        }

        private void statement(Node node)
        {
            switch (node)
            {
                case Block block:
                    foreach (var each in block.Body.OfType<Function>())
                        declare(each);
                    foreach (var each in block.Body.Where(static each => each is not Function))
                        statement(each);
                    break;
                case Function function:
                    declare(function);
                    break;
                case Var variables:
                    foreach (var (name, value) in variables.Items)
                    {
                        pushText(name);
                        if (value is null)
                            op(DefineLocal2);
                        else
                        {
                            expression(value);
                            op(DefineLocal);
                        }
                    }
                    break;
                case Evaluate evaluate:
                    discard(evaluate.Expression);
                    break;
                case If branch:
                    {
                        int otherwise = label(), end = label();
                        expression(branch.Test);
                        op(Not);
                        jump(JumpIf, otherwise, branch);
                        statement(branch.Then);
                        if (branch.Else is not null)
                            jump(JumpAlways, end, branch);
                        mark(otherwise);
                        if (branch.Else is not null)
                            statement(branch.Else);
                        mark(end);
                        break;
                    }
                case Loop loop:
                    {
                        int start = label(), next = label(), end = label();
                        if (loop.Before is not null)
                            statement(loop.Before);
                        mark(start);
                        if (loop.TestFirst && loop.Test is not null)
                        {
                            expression(loop.Test);
                            op(Not);
                            jump(JumpIf, end, loop);
                        }
                        Now.Exits.Push((end, next));
                        statement(loop.Body);
                        Now.Exits.Pop();
                        mark(next);
                        if (loop.After is not null)
                            statement(loop.After);
                        if (loop.TestFirst)
                            jump(JumpAlways, start, loop);
                        else
                        {
                            expression(loop.Test!);
                            jump(JumpIf, start, loop);
                        }
                        mark(end);
                        break;
                    }
                case Jump exit:
                    {
                        // 'continue' skips the switches it sits in, to the loop around them.
                        var target = exit.Break ? Now.Exits.Select(static each => each.Break).FirstOrDefault(-1)
                            : Now.Exits.Select(static each => each.Continue).FirstOrDefault(static each => each >= 0, -1);
                        if (target < 0)
                            throw error(exit, $"'{exit.At.Value}' is outside a loop.");
                        jump(JumpAlways, target, exit);
                        break;
                    }
                case Return result:
                    if (!Now.IsFunction)
                        throw error(result, "'return' is outside a function.");
                    if (result.Value is null)
                        pushConstant("undefined");
                    else
                        expression(result.Value);
                    op(ReturnValue);
                    break;
                case Switch choice:
                    {
                        // The value is kept in a hidden local for the tests.
                        var hidden = $"$switch{Now.Switches++}";
                        pushText(hidden);
                        expression(choice.Value);
                        op(DefineLocal);
                        var end = label();
                        var labels = choice.Cases.Select(_ => label()).ToArray();
                        for (var index = 0; index < choice.Cases.Count; index++)
                        {
                            if (choice.Cases[index].Test is not { } test)
                                continue;
                            pushText(hidden);
                            op(GetVariable);
                            expression(test);
                            op(StrictEquals);
                            jump(JumpIf, labels[index], test);
                        }
                        var fallback = choice.Cases.FindIndex(static each => each.Test is null);
                        jump(JumpAlways, fallback < 0 ? end : labels[fallback], choice);
                        Now.Exits.Push((end, -1));
                        for (var index = 0; index < choice.Cases.Count; index++)
                        {
                            mark(labels[index]);
                            choice.Cases[index].Body.ForEach(statement);
                        }
                        Now.Exits.Pop();
                        mark(end);
                        Now.Switches--;
                        break;
                    }
                case Class declaration:
                    classDeclaration(declaration);
                    break;
                default:
                    throw error(node, "This cannot be used as a statement.");
            }
        }

        // _global.Name = constructor; Name extends Super; Name.prototype.member = value; Name.member = value (static).
        private void classDeclaration(Class declaration)
        {
            if (_class is not null || _codes.Count > 1)
                throw error(declaration, "A class is declared at the top of a script, not inside a function or another class.");
            _class = declaration;
            var constructor = declaration.Constructor ?? new Function(declaration.At, null, [], []);
            // A subclass constructor that does not call super(...) itself calls it first, without arguments.
            if (declaration.Super is not null
                && !constructor.Body.Any(static each => each is Evaluate { Expression: Call { Callee: Name { Value: "super" } } }))
            {
                var at = declaration.At;
                constructor = constructor with { Body = [new Evaluate(at, new Call(at, new Name(at, "super"), [], false)), .. constructor.Body] };
            }
            void theClass()
            {
                pushText("_global");
                op(GetVariable);
                pushText(declaration.Name);
                op(GetMember);
            }
            pushText("_global");
            op(GetVariable);
            pushText(declaration.Name);
            function(constructor);
            op(SetMember);
            if (declaration.Super is not null)
            {
                theClass();
                expression(declaration.Super);
                op(Extends);
            }
            foreach (var (name, value, isStatic) in declaration.Members)
            {
                theClass();
                if (!isStatic)
                {
                    pushText("prototype");
                    op(GetMember);
                }
                pushText(name);
                expression(value);
                op(SetMember);
            }
            _class = null;
        }

        // ---- expressions

        private void function(Function function)
        {
            var code = new Code { IsFunction = true };
            foreach (var parameter in function.Parameters)
                code.Locals.Add(parameter);
            _codes.Push(code);
            body(function.Body);
            var bytes = finish(function.At);
            // DefineFunction2 without registers or preloads: this, arguments and super are locals by name.
            var header = new byte[2 + 2 + 1 + 2 + function.Parameters.Count * 3 + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(header, intern(function.Name ?? ""));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)function.Parameters.Count);
            for (var index = 0; index < function.Parameters.Count; index++)
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8 + index * 3), intern(function.Parameters[index]));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(header.Length - 2), (ushort)bytes.Length);
            Now.Bytes.Add(DefineFunction2);
            Now.Bytes.Add((byte)header.Length);
            Now.Bytes.Add((byte)(header.Length >> 8));
            Now.Bytes.AddRange(header);
            Now.Bytes.AddRange(bytes);
        }

        // An expression whose value nobody uses.
        private void discard(Node node)
        {
            switch (node)
            {
                case Assign assign:
                    store(assign.Target, assign.Operator, assign.Value);
                    break;
                case Step step:
                    this.step(step);
                    break;
                case Sequence sequence:
                    sequence.Items.ForEach(discard);
                    break;
                default:
                    expression(node);
                    op(Pop);
                    break;
            }
        }

        private void arguments(List<Node> list)
        {
            for (var index = list.Count - 1; index >= 0; index--)
                expression(list[index]);
            pushNumber(list.Count);
        }

        private void read(Node target)
        {
            if (target is Name name && resolve(name) is Name plain)
            {
                pushText(plain.Value);
                op(GetVariable);
                return;
            }
            var member = (Member)(target is Name unresolved ? resolve(unresolved) : target);
            expression(member.Target);
            expression(member.Key);
            op(GetMember);
        }

        // target = value, or target op= value. ponytail: the target's object and key are evaluated twice for
        // "op=" and "++" (a[i++] += 1 steps i twice); a temporary when a script needs otherwise.
        private void store(Node target, string operation, Node? value, byte? stepOperation = null)
        {
            var resolved = target is Name name ? resolve(name) : target;
            if (resolved is Name variable)
                pushText(variable.Value);
            else
            {
                expression(((Member)resolved).Target);
                expression(((Member)resolved).Key);
            }
            if (stepOperation is { } stepCode)
            {
                read(target);
                op(stepCode);
            }
            else if (operation == "=")
                expression(value!);
            else
            {
                read(target);
                expression(value!);
                op(binaryOperation(operation[..^1]));
            }
            op(resolved is Name ? SetVariable : SetMember);
        }

        private void step(Step step) => store(step.Target, "", null, step.Increment ? Increment : Decrement);

        private static byte binaryOperation(string operation) => operation switch
        {
            "+" => Add, "-" => Subtract, "*" => Multiply, "/" => Divide, "%" => Modulo, "&" => BitAnd, "|" => BitOr, "^" => BitXor,
            "<<" => ShiftLeft, ">>" => ShiftRight, ">>>" => ShiftRightUnsigned, "<" or ">=" => Less, ">" or "<=" => Greater,
            "==" or "!=" => EqualsLoose, _ => StrictEquals,
        };

        private void expression(Node node)
        {
            switch (node)
            {
                case Number number:
                    pushNumber(number.Value);
                    break;
                case Text text:
                    pushText(text.Value);
                    break;
                case Constant constant:
                    pushConstant(constant.Name);
                    break;
                case Name or Member:
                    read(node);
                    break;
                case ArrayOf array:
                    arguments(array.Items);
                    op(InitArray);
                    break;
                case ObjectOf value:
                    foreach (var (key, item) in value.Items)
                    {
                        pushText(key);
                        expression(item);
                    }
                    pushNumber(value.Items.Count);
                    op(InitObject);
                    break;
                case Function function:
                    this.function(function);
                    break;
                case Sequence sequence:
                    foreach (var each in sequence.Items.SkipLast(1))
                        discard(each);
                    expression(sequence.Items[^1]);
                    break;
                case Assign assign:
                    store(assign.Target, assign.Operator, assign.Value);
                    read(assign.Target);
                    break;
                case Step step:
                    if (step.Prefix)
                    {
                        this.step(step);
                        read(step.Target);
                    }
                    else
                    {
                        read(step.Target);
                        op(ToNumber);
                        this.step(step);
                    }
                    break;
                case Conditional conditional:
                    {
                        int otherwise = label(), end = label();
                        expression(conditional.Test);
                        op(Not);
                        jump(JumpIf, otherwise, conditional);
                        expression(conditional.Then);
                        jump(JumpAlways, end, conditional);
                        mark(otherwise);
                        expression(conditional.Else);
                        mark(end);
                        break;
                    }
                case Unary unary:
                    switch (unary.Operator)
                    {
                        case "!":
                            expression(unary.Operand);
                            op(Not);
                            break;
                        case "-" when unary.Operand is Number literal:
                            pushNumber(-literal.Value);
                            break;
                        case "-":
                            pushNumber(0);
                            expression(unary.Operand);
                            op(Subtract);
                            break;
                        case "+":
                            expression(unary.Operand);
                            op(ToNumber);
                            break;
                        case "~":
                            expression(unary.Operand);
                            pushNumber(-1);
                            op(BitXor);
                            break;
                        default: // delete
                            expression(((Member)unary.Operand).Target);
                            expression(((Member)unary.Operand).Key);
                            op(Delete);
                            break;
                    }
                    break;
                case Binary { Operator: "&&" or "||" } logical:
                    {
                        // The left value is the result unless the right one is needed.
                        var end = label();
                        expression(logical.Left);
                        op(Duplicate);
                        if (logical.Operator == "&&")
                            op(Not);
                        jump(JumpIf, end, logical);
                        op(Pop);
                        expression(logical.Right);
                        mark(end);
                        break;
                    }
                case Binary binary:
                    expression(binary.Left);
                    expression(binary.Right);
                    op(binaryOperation(binary.Operator));
                    if (binary.Operator is "!=" or "!==" or "<=" or ">=")
                        op(Not);
                    break;
                case Call call:
                    this.call(call);
                    break;
                default:
                    throw error(node, "This cannot be used as a value.");
            }
        }

        private void call(Call call)
        {
            var callee = call.Callee is Name name ? resolve(name) : call.Callee;
            if (callee is Name { Value: "trace" } && !call.Construct && call.Arguments.Count == 1)
            {
                expression(call.Arguments[0]);
                op(Trace);
                pushConstant("undefined");
                return;
            }
            // On a timeline these are instructions, not functions.
            if (callee is Name { Value: "stop" or "play" } playback && !call.Construct && call.Arguments.Count == 0)
            {
                op(playback.Value == "stop" ? Stop : Play);
                pushConstant("undefined");
                return;
            }
            if (callee is Name { Value: "gotoAndStop" or "gotoAndPlay" } seek && !call.Construct && call.Arguments.Count == 1)
            {
                expression(call.Arguments[0]);
                Now.Bytes.AddRange([GotoFrame2, 1, 0, (byte)(seek.Value == "gotoAndPlay" ? 1 : 0)]);
                pushConstant("undefined");
                return;
            }
            if (!call.Construct && callee is not (Name or Member))
            {
                // A function that is a value, as in f()(1): kept in a hidden local, then called by that name.
                var hidden = $"$call{Now.Calls++}";
                pushText(hidden);
                expression(callee);
                op(DefineLocal);
                arguments(call.Arguments);
                pushText(hidden);
                op(CallFunction);
                Now.Calls--;
                return;
            }
            arguments(call.Arguments);
            switch (callee)
            {
                case Name { Value: not "super" } function:
                    pushText(function.Value);
                    op(call.Construct ? NewObject : CallFunction);
                    break;
                case Member member:
                    expression(member.Target);
                    expression(member.Key);
                    op(call.Construct ? NewMethod : CallMethod);
                    break;
                default:
                    // super(...): the object called as itself, with no method name.
                    if (call.Construct)
                        throw error(call, "'new' needs a class name.");
                    expression(callee);
                    pushConstant("undefined");
                    op(CallMethod);
                    break;
            }
        }
    }
}
