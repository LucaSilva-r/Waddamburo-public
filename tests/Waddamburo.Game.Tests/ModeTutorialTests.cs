using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class ModeTutorialTests
{
    private static readonly string[] HostNames = ["", "Lumen"];

    [Fact]
    public void WaiwaiRoutesVoicesEffectsAndCompletion()
    {
        var cues = new List<(string, int)>();
        var host = new ModeTutorialHostBinding(false, 0, true, (bank, cue) => cues.Add((bank, cue)));
        var player = new LumenPlayer(movie(("PlayVO", [2]), ("PlaySE", [11, -1]), ("AllFinish", [])),
            1280, 720, rootCharacterId: 1, hostBinding: host);
        host.Attach(player);
        for (var i = 0; i < 3; i++) { player.Advance(); host.Poll(); }
        Assert.Equal(new[] { ("VO_WAIATTR", 3), ("SE_WAITUTOR", 12) }, cues);
        Assert.True(host.Finished);
    }

    [Fact]
    public void AiSoundRoutingDoesNotUseResultVoices()
    {
        var cues = new List<(string, int)>();
        var host = new ModeTutorialHostBinding(true, 1, false, (bank, cue) => cues.Add((bank, cue)));
        var player = new LumenPlayer(movie(("PlaySound", [415, 3]), ("PlaySound", [1, 0]), ("AllFinish", [])),
            1280, 720, rootCharacterId: 1, hostBinding: host);
        host.Attach(player);
        for (var i = 0; i < 3; i++) { player.Advance(); host.Poll(); }
        Assert.Equal(new[] { ("VO_AIBTL_INFO", 3), ("SE_COM", 0) }, cues);
        Assert.True(host.Finished);
    }

    [Fact]
    public void RepeatedStreamRequestsStartPreparedMusicOnlyOnce()
    {
        var prepared = new List<string>();
        var starts = 0;
        var host = new ModeTutorialHostBinding(true, 0, false, prepareMusic: name => { prepared.Add(name); return true; },
            playMusic: () => starts++);
        var player = new LumenPlayer(movie(("SetBGM", [-777]), ("PlayStream", []), ("PlayStream", []),
            ("SetBGM", [-777]), ("PlayStream", [])), 1280, 720, rootCharacterId: 1, hostBinding: host);
        host.Attach(player);
        for (var i = 0; i < 5; i++) { player.Advance(); host.Poll(); }
        Assert.Equal("SyntheticTrack", Assert.Single(prepared));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(9, 10)]
    [InlineData(10, 11)]
    [InlineData(11, 12)]
    [InlineData(12, 13)]
    [InlineData(13, 15)]
    [InlineData(14, 17)]
    public void WaiwaiEffectRequestsMatchObservedCues(int request, int cue) =>
        Assert.Equal(cue, ModeTutorialHostBinding.WaiwaiEffectCue(request));

    private static LmbMovieDefinition movie(params (string Name, int[] Arguments)[] calls)
    {
        var names = HostNames.Append("SyntheticTrack").Concat(calls.Select(static call => call.Name)).Distinct().ToArray();
        Avm1Instruction op(byte code, Avm1Operand? operand = null) => new(code, 0, 1, [], operand, null, null);
        Avm1Instruction push(params Avm1PushValue[] values) => op(0x96, new Avm1PushOperand([.. values]));
        Avm1PushStringValue name(string value) => new(8, (ushort)Array.IndexOf(names, value));
        var actions = calls.Select((call, index) =>
        {
            var instructions = new[]
            {
                push([.. call.Arguments.Reverse().Select(value => value == -777 ? name("SyntheticTrack") : (Avm1PushValue)new Avm1PushIntegerValue(value))]),
                push(new Avm1PushIntegerValue(call.Arguments.Length), name("Lumen")), op(0x1C),
                push(name(call.Name)), op(0x52), op(0x17), op(0),
            };
            return new LmbAction(index, [], 0)
            {
                Code = new Avm1CodeBlock(0, instructions.Length, [.. instructions.Select((instruction, offset) => instruction with { Offset = offset })]),
            };
        }).ToImmutableArray();
        var frames = new List<LmbTimelineCommand> { new LmbShowFrameCommand(0, 0, [], null!) };
        for (var index = 0; index < calls.Length; index++)
        {
            frames.Add(new LmbShowFrameCommand((uint)index + 1, 1, [], null!));
            frames.Add(new LmbDoActionCommand((uint)index, 0, [], null!));
        }
        return new LmbMovieDefinition(null!, null, [.. names.Select((value, index) => new LmbString(index, value, 0))],
            [], [], [], [], [], actions, [], [new LmbSpriteDefinition(1, 0, (uint)calls.Length + 1, 0, 0, [], [.. frames], null!)], []);
    }

}
