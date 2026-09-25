using Waddamburo.Catalog;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class TaikoLongNotePresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BalloonStartsOnceCountsDownAndEndsOnce(bool pop)
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Balloon, 2);
        var session = sessionFor(chart);
        var layer = movie(calls);
        var don = new DonController();
        var completed = new List<(PlayableLongNoteKind Kind, bool Succeeded)>();
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => layer, layer, layer,
            don: don, onBalloonCompleted: (kind, succeeded) => completed.Add((kind, succeeded)),
            onNoteStarted: started.Add);
        Assert.False(presentation.BalloonVisible);
        presentation.Update(TimeSpan.FromSeconds(1));
        Assert.True(presentation.BalloonVisible);
        presentation.Update(TimeSpan.FromSeconds(1.1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.2));
        if (pop) session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.3));
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(4));
        Assert.Equal(new (string, double)[] { ("Reset", double.NaN), ("SetGekiRendaCount", 0),
            ("SetGekiRendaCount", 2), ("SetGekiRendaCount", 1), ("GekiRendaEnd", pop ? 0 : 1) }, calls);
        Assert.Empty(layer.Player.Diagnostics);
        Assert.True(presentation.BalloonVisible);
        Assert.Equal(pop ? "don_balloon_success" : "don_balloon_failure", don.Motions[^1].OneShot);
        Assert.Equal([(PlayableLongNoteKind.Balloon, pop)], completed);
        presentation.AdvanceAnimations();
        Assert.False(presentation.BalloonVisible);
        Assert.Equal(pop ? "don_balloon_success" : "don_balloon_failure", don.Motions[^1].OneShot);
        var motionCount = don.Motions.Count;
        presentation.AdvanceAnimations();
        Assert.Equal(motionCount, don.Motions.Count);
        Assert.Single(completed);
        Assert.Equal([PlayableLongNoteKind.Balloon], started);
    }

    [Fact]
    public void KusudamaCompletionReportsItsOwnKind()
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Kusudama, 1);
        var session = sessionFor(chart);
        var layer = movie(calls);
        var completed = new List<(PlayableLongNoteKind Kind, bool Succeeded)>();
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => layer, layer, layer,
            kusudama: layer, onBalloonCompleted: (kind, succeeded) => completed.Add((kind, succeeded)),
            onNoteStarted: started.Add);
        presentation.Update(TimeSpan.FromSeconds(1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.1));
        presentation.Update(TimeSpan.FromSeconds(3));
        Assert.Equal([(PlayableLongNoteKind.Kusudama, true)], completed);
        Assert.Contains(("EndResult", 0d), calls);
        Assert.Equal([PlayableLongNoteKind.Kusudama], started);
    }

    [Fact]
    public void RollsHaveIndependentWidthsAndCountersReceiveEachHitOnce()
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(PlayableLongNoteKind.Roll);
        var session = sessionFor(chart);
        var layer = movie(calls);
        var started = new List<PlayableLongNoteKind>();
        var presentation = new TaikoLongNotePresentation(chart, session, _ => movie(calls), layer, layer,
            onNoteStarted: started.Add);
        var noteLayer = Assert.Single(presentation.NoteLayers(TimeSpan.Zero, (time, _) => 400 + (float)time.TotalSeconds * 100, 400, 250)).Layer;
        Assert.NotSame(layer.Player, noteLayer.Player);
        Assert.Contains(("SetWidth", 100d), calls);
        presentation.Update(TimeSpan.FromSeconds(1));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.1));
        session.SubmitInput(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1.2));
        presentation.AdvanceAnimations();
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        presentation.Update(TimeSpan.FromSeconds(3));
        Assert.Equal([1d, 2d], calls.Where(c => c.Item1 == "SetRendaCount").Select(c => c.Item2));
        Assert.Equal(2, calls.Count(c => c.Item1 == "HitAction"));
        Assert.Single(calls, c => c.Item1 == "RendaEnd");
        Assert.Contains(calls, c => c.Item1 == "OnUpdate");
        Assert.Equal([PlayableLongNoteKind.Roll], started);
    }

    [Theory]
    [InlineData(PlayableLongNoteKind.Roll)]
    [InlineData(PlayableLongNoteKind.BigRoll)]
    public void RollTailRemainsVisibleAfterEndUntilItLeavesTheScreen(PlayableLongNoteKind kind)
    {
        var calls = new List<(string, double)>();
        var chart = chartFor(kind);
        var layer = movie(calls);
        var presentation = new TaikoLongNotePresentation(chart, sessionFor(chart), _ => movie(calls), layer, layer);

        float position(TimeSpan noteTime, TimeSpan _) => 400 + (float)(noteTime - TimeSpan.FromSeconds(6.1)).TotalSeconds * 100;
        var visible = Assert.Single(presentation.NoteLayers(TimeSpan.FromSeconds(6.1), position, 400, 250));
        Assert.Equal(-110, visible.Layer.Transform.X);
        Assert.Equal(100, calls.Last(call => call.Item1 == "SetWidth").Item2);

        float offscreen(TimeSpan noteTime, TimeSpan _) => 400 + (float)(noteTime - TimeSpan.FromSeconds(7.1)).TotalSeconds * 100;
        Assert.Empty(presentation.NoteLayers(TimeSpan.FromSeconds(7.1), offscreen, 400, 250));
    }

    private static PlayableChart chartFor(PlayableLongNoteKind kind, int quota = 0) => new(
        new(new(SongSourceKind.Tja, "synthetic"), "presentation"), TimeSpan.Zero, TimeSpan.FromSeconds(3), [],
        [new(TimeSpan.Zero, 120, 4, 4)], [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], [],
        [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), kind, quota)]);

    private static TaikoJudgementSession sessionFor(PlayableChart chart) => new(chart,
        new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)), TimeSpan.Zero);

    // Original synthetic callback movie: each exported callback forwards its name/argument to a recording host.
    private static LumenSceneLayer movie(List<(string, double)> calls)
    {
        var names = new[] { "level01", "n", "Record", "flash", "external", "ExternalInterface", "addCallback",
            "Reset", "SetGekiRendaCount", "GekiRendaEnd", "SetRendaCount", "RendaEnd", "SetWidth", "HitAction", "OnUpdate",
            "AddNorma", "SetGogoTime", "SetCount", "EndResult", "SetPlayerNum", "SetWaiWai" };
        var code = new List<Avm1Instruction>();
        var offset = 0;
        Avm1Instruction op(byte opcode, Avm1Operand? operand = null, Avm1CodeBlock? body = null) =>
            new(opcode, offset++, 1, [], operand, null, body);
        Avm1Instruction push(params Avm1PushValue[] values) => op(0x96, new Avm1PushOperand([.. values]));
        Avm1PushStringValue str(int index) => new(8, (ushort)index);
        for (var index = 7; index < names.Length; index++)
        {
            var body = new Avm1CodeBlock(0, 6, [push(str(1)), op(0x1C),
                push(str(index), new Avm1PushIntegerValue(2), str(2)), op(0x3D), op(0x17), op(0)]);
            body = body with { Instructions = [.. body.Instructions.Select((instruction, i) => instruction with { Offset = i })] };
            code.Add(op(0x9B, new Avm1FunctionOperand((ushort)index, 0, 0, [new(null, 1)], 6), body));
            code.Add(push(str(index))); code.Add(op(0x1C));
            code.Add(push(new Avm1PushNullValue(), str(index), new Avm1PushIntegerValue(3), str(3)));
            code.Add(op(0x1C)); code.Add(push(str(4))); code.Add(op(0x4E));
            code.Add(push(str(5))); code.Add(op(0x4E)); code.Add(push(str(6))); code.Add(op(0x52)); code.Add(op(0x17));
        }
        code.Add(op(0x07)); code.Add(op(0));
        var action = new LmbAction(0, [], 0) { Code = new(0, code.Count, [.. code.Select((instruction, i) => instruction with { Offset = i })]) };
        var sprite = new LmbSpriteDefinition(1, 1, 1, 1, 0, [], [new LmbFrameLabelCommand(0, 0, 0, [], null!),
            new LmbShowFrameCommand(0, 1, [], null!), new LmbDoActionCommand(0, 0, [], null!)], null!);
        var movie = new LmbMovieDefinition(null!, null, [.. names.Select((name, index) => new LmbString(index, name, 0))],
            [], [], [], [], [], [action], [], [sprite], []);
        return new(new LumenPlayer(movie, 1280, 720, rootCharacterId: 1, hostBinding: new Recorder(calls)), LumenMatrix.Identity, 0, 0);
    }

    private sealed class DonController : IDonPresentationController
    {
        public List<DonMotionRequest> Motions { get; } = [];
        public void Reset(DonPresentationLayout layout) { }
        public LumenNativeSurfaceKey GetSurface(int playerIndex) => new($"synthetic:{playerIndex}");
        public void SetMotion(DonMotionRequest request) => Motions.Add(request);
    }

    private sealed class Recorder(List<(string, double)> calls) : ILumenHostBinding
    {
        public void Install(LumenHostContext context) => context.RegisterFunction("Record", call =>
        {
            calls.Add((call.Arguments[0].AsString(), call.Arguments[1].Kind == LumenHostValueKind.Number
                ? call.Arguments[1].AsNumber() : double.NaN));
            return LumenHostValue.Undefined;
        });
    }
}
