using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Tests;

public sealed class Avm1AssemblyTests
{
    [Fact]
    public void NestedFunctionsOpaqueOperandsAndBranchesRoundTripWithoutChangingBytes()
    {
        const string source = """
            start:
            Push int:42 string:0 bool:true double:1.25 null undefined register:2
            If finish
            DefineFunction 000000000000 {
              inside:
              Op70
              OpFE 123456
              Jump done
              Play
              done:
              Stop
            }
            Jump start
            finish:
            End
            """;
        var bytes = Avm1Assembly.Assemble(source);
        var document = new LmbEditDocument(LmbEditDocumentTests.Movie());
        var index = document.AddAction(2, 0, bytes);
        var action = document.Definition.Actions[index];
        Assert.Equal(bytes, Avm1Assembly.Assemble(Avm1Assembly.Disassemble(action, document.Definition.Strings)));
        Assert.NotNull(action.Code.Instructions.Single(i => i.Opcode == 0x9B).Body);
        Assert.Contains(document.Definition.Sprites[0].Timeline, c => c is LmbDoActionCommand { ActionIndex: 0 });
    }

    [Fact]
    public void EditingBodyRecalculatesLexicalLengthAndEnclosingBranchDisplacements()
    {
        var document = new LmbEditDocument(LmbEditDocumentTests.Movie());
        var bytes = Avm1Assembly.Assemble("Jump end\nWith 0000 {\nStop\n}\nend:\nEnd");
        document.AddAction(2, 0, bytes);
        var edited = Avm1Assembly.Disassemble(document.Definition.Actions[0]).Replace("Stop", "Play\n  Stop", StringComparison.Ordinal);
        document.SetActionBytecode(0, Avm1Assembly.Assemble(edited));
        var instructions = document.Definition.Actions[0].Code.Instructions;
        Assert.Equal(2, instructions[1].Body!.Length);
        Assert.Equal(instructions[2].Offset, instructions[0].BranchTarget);
        var output = new LmbEditDocument(document.Export(false));
        Assert.Equal(document.Definition.Actions[0].Bytecode.ToArray(), output.Definition.Actions[0].Bytecode.ToArray());
    }

    [Theory]
    [InlineData("Jump missing")]
    [InlineData("label:\nlabel:\nEnd")]
    [InlineData("With 0000 {\nStop")]
    [InlineData("Stop FF")]
    [InlineData("Push 01")]
    [InlineData("With 0000 {\nJump outside\n}\noutside:\nEnd")]
    public void InvalidAssemblyIsRejected(string source) => Assert.Throws<FormatException>(() => Avm1Assembly.Assemble(source));

    [Fact]
    public void ReplacingOneActionRetainsOtherEntriesAndRejectsInvalidStringReferencesAtomically()
    {
        var document = new LmbEditDocument(LmbEditDocumentTests.Movie());
        document.AddAction(2, 0, Avm1Assembly.Assemble("Stop\nEnd"));
        document.AddAction(2, 1, Avm1Assembly.Assemble("Play\nEnd"));
        var other = document.Definition.Actions[1].Bytecode.ToArray();
        document.SetActionBytecode(0, Avm1Assembly.Assemble("Push int:123\nPop\nEnd"));
        Assert.Equal(other, document.Definition.Actions[1].Bytecode.ToArray());
        var before = document.Data.ToArray();
        Assert.Throws<InvalidDataException>(() => document.SetActionBytecode(0, Avm1Assembly.Assemble("Push string:65535\nEnd")));
        Assert.Equal(before, document.Data.ToArray());
        document.Undo();
        Assert.Equal(new byte[] { 0x07, 0x00 }, document.Definition.Actions[0].Bytecode.ToArray());
    }
}
