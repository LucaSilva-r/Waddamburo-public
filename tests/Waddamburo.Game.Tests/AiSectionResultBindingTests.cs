using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class AiSectionResultBindingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultsWaitForSetupThenStartOnceWithProvisionalProgress(bool ranked)
    {
        var calls = new List<(string Name, ImmutableArray<LumenHostValue> Arguments)>();
        var outcome = new AiBattleOutcome([new(true, default, default), new(false, default, default), new(true, default, default)]);
        var numbers = new AiResultNumbers(2, 3, 12, 3, true, true, 15, 13, 20) { ShowProgress = ranked };
        var binding = new AiSectionResultBinding(outcome, numbers, null);
        var player = new LumenPlayer(movie(), 1280, 720, rootCharacterId: 1, hostBinding: new RecordingHost(binding, calls));
        binding.Attach(player);

        // A normal result can close before the last section-result callback is registered.
        binding.Start();
        Assert.Empty(calls);
        player.Advance();
        binding.Poll();
        binding.Poll();

        Assert.Equal(2, calls.Single(call => call.Name == "SetRankNumber").Arguments[0].AsNumber());
        Assert.Equal(3, calls.Single(call => call.Name == "SetRankNumber").Arguments[1].AsNumber());
        Assert.False(calls.Single(call => call.Name == "SetRankNumber").Arguments[2].AsBoolean());
        Assert.Equal(new[] { 13d, ranked ? 1d : 0d }, calls.Single(call => call.Name == "SetWinNumber").Arguments.Select(value => value.AsNumber()));
        Assert.Equal(15, Assert.Single(calls.Single(call => call.Name == "SetNextRankNumber").Arguments).AsNumber());
        Assert.Equal(20, calls.Single(call => call.Name == "SetNextRewardNumber").Arguments[0].AsNumber());
        Assert.Equal("Start", calls[^1].Name);
        Assert.Equal(5, calls.Count);
        // The rank board stays: the movie is fed the numbers, never patched (an unranked battle gains nothing).
        Assert.Single(player.CreateRenderSnapshot().Quads);
    }

    [Theory]
    [InlineData(0, "VO_COM")]
    [InlineData(1, "SE_COM")]
    [InlineData(420, "VO_AIBTL_RSLT")]
    [InlineData(421, "SE_AIBTL_RSLT")] // traced: PlaySound(421, 0/1/4) → SE_AIBTL_RSLT 0/1/4
    public void SoundCallTypesFollowTheMoviesSoundConstants(int callType, string bank) =>
        Assert.Equal(bank, AiSectionResultBinding.Bank(callType));

    private static readonly (string Name, int Parameters)[] Methods =
        [("SetRankNumber", 3), ("SetNextRankNumber", 1), ("SetNextRewardNumber", 2), ("Start", 0), ("SetWinNumber", 2)];

    private sealed class RecordingHost(AiSectionResultBinding binding, List<(string, ImmutableArray<LumenHostValue>)> calls) : ILumenHostBinding
    {
        public void Install(LumenHostContext context)
        {
            binding.Install(context);
            context.RegisterObject("Recorder", recorder =>
            {
                foreach (var (name, _) in Methods)
                    recorder.RegisterMethod(name, call => { calls.Add((name, call.Arguments)); return LumenHostValue.Undefined; });
            });
        }
    }

    private static LmbMovieDefinition movie()
    {
        string[] names = ["", "a", "b", "c", "Recorder", "flash", "external", "ExternalInterface", "addCallback",
            "SetRankNumber", "SetNextRankNumber", "SetNextRewardNumber", "Start", "SetWinNumber", "main", "panel", "rank_reward_board"];
        Avm1Instruction op(byte code, Avm1Operand? operand = null, Avm1CodeBlock? body = null) => new(code, 0, 1, [], operand, null, body);
        Avm1Instruction push(params Avm1PushValue[] values) => op(0x96, new Avm1PushOperand([.. values]));
        Avm1PushStringValue str(int index) => new(8, (ushort)index);
        Avm1CodeBlock block(List<Avm1Instruction> code) => new(0, code.Count, [.. code.Select((instruction, index) => instruction with { Offset = index })]);
        LmbAction callbacks(int index, IEnumerable<int> methods)
        {
            var code = new List<Avm1Instruction>();
            foreach (var method in methods)
            {
                var count = Methods[method].Parameters;
                var body = new List<Avm1Instruction>();
                for (var parameter = count; parameter > 0; parameter--) { body.Add(push(str(parameter))); body.Add(op(0x1C)); }
                body.Add(push(new Avm1PushIntegerValue(count), str(4))); body.Add(op(0x1C)); body.Add(push(str(9 + method)));
                body.Add(op(0x52)); body.Add(op(0x17)); body.Add(op(0));
                code.Add(op(0x9B, new Avm1FunctionOperand((ushort)(9 + method), 0, 0,
                    [.. Enumerable.Range(1, count).Select(parameter => new Avm1FunctionParameter(null, (ushort)parameter))], (ushort)body.Count), block(body)));
                code.Add(push(str(9 + method))); code.Add(op(0x1C));
                code.Add(push(new Avm1PushNullValue(), str(9 + method), new Avm1PushIntegerValue(3), str(5))); code.Add(op(0x1C));
                code.Add(push(str(6))); code.Add(op(0x4E)); code.Add(push(str(7))); code.Add(op(0x4E));
                code.Add(push(str(8))); code.Add(op(0x52)); code.Add(op(0x17));
            }
            code.Add(op(0));
            return new(index, [], 0) { Code = block(code) };
        }
        LmbPlaceObjectCommand place(uint id, uint name) => new(id, 1, name, 1, 0, 1, 0, 0, 0, uint.MaxValue, uint.MaxValue, [], null!);
        LmbSpriteDefinition container(uint id, uint child, uint name) => new(id, 0, 1, 0, 0, [],
            [new LmbShowFrameCommand(0, 1, [], null!), place(child, name)], null!);
        var root = new LmbSpriteDefinition(1, 0, 2, 0, 0, [], [new LmbShowFrameCommand(0, 2, [], null!),
            place(2, 14), new LmbDoActionCommand(0, 0, [], null!), new LmbShowFrameCommand(1, 1, [], null!), new LmbDoActionCommand(1, 0, [], null!)], null!);
        var shape = new LmbShapeDefinition(5, 1, [], [new LmbShapeGeometry(
            [new(0, 0, 0, 0), new(10, 0, 1, 0), new(10, 10, 1, 1), new(0, 10, 0, 1)], 1, 0x00010000, [], null!)], null!);
        return new(null!, null, [.. names.Select((name, index) => new LmbString(index, name, 0))], [], [new(1, 0, 0, 1, 0, 0)], [], [], [],
            [callbacks(0, [0, 1, 2, 3]), callbacks(1, [4])], [shape],
            [root, container(2, 3, 15), container(3, 4, 16), container(4, 5, 0)], []);
    }
}
