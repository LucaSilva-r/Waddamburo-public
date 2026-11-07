using Waddamburo.Catalog;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class LiveEffectsTests
{
    [Fact]
    public void EditorBoundsUseTheLiveSceneRolesPivotsAndCameraTransforms()
    {
        var chart = new PlayableChart(new(new(SongSourceKind.Tja, "synthetic"), "oni"), TimeSpan.Zero, TimeSpan.FromSeconds(20),
            [], [new(TimeSpan.Zero, 120, 4, 4)], [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], []);
        var session = new TaikoJudgementSession(chart, new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)));
        var initialTime = session.CurrentTime;
        var dancers = Marker(); var don = Marker(); var lane = Marker();
        var presentation = new TaikoLumenPresentation(chart, session, [dancers], [],
            new Dictionary<PlayableNoteKind, LumenSceneLayer>(), lane, lane, lane.Player, lane.Player,
            roles: new Dictionary<LumenPlayer, string> { [dancers.Player] = "dance" });
        var initial = presentation.EffectElements(TimeSpan.Zero, don);
        Assert.Equal(2, initial.Count);
        Assert.Contains(initial, e => e.Target == "dancers" && e.Minimum.X == 396 && e.Pivot.X == 400);
        Assert.Contains(initial, e => e.Target == "don");
        presentation.ReplaceEffects(new() { Mods = [new(TimeSpan.Zero, TimeSpan.Zero, "dancers", "x", 100, ChartEase.Linear),
            new(TimeSpan.Zero, TimeSpan.Zero, "camera", "scale", 2, ChartEase.Linear)] });
        var moved = Assert.Single(presentation.EffectElements(TimeSpan.Zero), e => e.Target == "dancers");
        Assert.Equal(352, moved.Minimum.X, 4); Assert.Equal(368, moved.Maximum.X, 4);
        Assert.Equal(360, moved.Pivot.X, 4); Assert.Equal(140, moved.Pivot.Y, 4);
        Assert.True(moved.Contains(new(360, 140)));
        Assert.Equal(2, moved.Parent.M11);
        Assert.Equal(initialTime, session.CurrentTime);
    }

    [Fact]
    public void ReplacingEffectsRebuildsVisibilityAndResetsTargetWithoutResettingJudgement()
    {
        var chart = new PlayableChart(new(new(SongSourceKind.Tja, "synthetic"), "oni"), TimeSpan.Zero, TimeSpan.FromSeconds(20),
            [new(TimeSpan.FromSeconds(1), PlayableNoteKind.Don), new(TimeSpan.FromSeconds(10), PlayableNoteKind.Don)], [new(TimeSpan.Zero, 120, 4, 4)],
            [new(TimeSpan.Zero, 1)], [new(TimeSpan.Zero, false)], [new(TimeSpan.FromSeconds(10), true)]);
        var session = new TaikoJudgementSession(chart, new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95)));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        var target = Marker();
        var note = Marker();
        var bar = Marker();
        var presentation = new TaikoLumenPresentation(chart, session, [], [],
            new Dictionary<PlayableNoteKind, LumenSceneLayer> { [PlayableNoteKind.Don] = note }, bar, target, target.Player, target.Player);
        var time = TimeSpan.FromSeconds(6);
        var before = presentation.CreateLayers(time).Back;
        Assert.DoesNotContain(before, layer => layer.Player == note.Player);
        Assert.DoesNotContain(before, layer => layer.Player == bar.Player);
        var effects = new ChartEffects
        {
            JudgeMoves = [new(TimeSpan.Zero, TimeSpan.Zero, 50, 20)],
            Mods = [new(TimeSpan.Zero, TimeSpan.Zero, "playfield", "x", -1000, ChartEase.Linear)],
        };
        presentation.ReplaceEffects(effects);
        presentation.Update(time);
        Assert.Equal((450f, 270f), presentation.HitPoint);
        var moved = presentation.CreateLayers(time).Back;
        Assert.Contains(moved, layer => layer.Player == note.Player);
        Assert.Contains(moved, layer => layer.Player == bar.Player);
        presentation.ReplaceEffects(ChartEffects.None);
        presentation.Update(time);
        Assert.Equal((400f, 250f), presentation.HitPoint);
        Assert.True(target.Player.TryGetInstanceBounds("target", out var restored));
        Assert.Equal(-4, restored.X);
        Assert.Equal(-4, restored.Y);
        Assert.DoesNotContain(presentation.CreateLayers(time).Back, layer => layer.Player == note.Player);
        Assert.Equal(TimeSpan.FromSeconds(1), session.CurrentTime);
        Assert.True(session.IsJudged(0));
        Assert.False(session.IsMissed(0));
    }

    private static LumenSceneLayer Marker()
    {
        var shape = new LmbShapeDefinition(2, 1, [], [new LmbShapeGeometry(
            [new(-4, -4, 0, 0), new(4, -4, 1, 0), new(4, 4, 1, 1), new(-4, 4, 0, 1)], 0, 1 << 16, [], null!)], null!);
        var sprite = new LmbSpriteDefinition(1, 1, 1, 1, 0, [],
            [new LmbFrameLabelCommand(1, 0, 0, [], null!), new LmbShowFrameCommand(0, 1, [], null!),
             new LmbPlaceObjectCommand(2, 1, 1, 1, 0, 1, 0, 0, 0, uint.MaxValue, uint.MaxValue, [], null!)], null!);
        var names = new[] { "", "target", "flash", "external", "ExternalInterface", "addCallback",
            "SetComboCount", "SetBranchAvailable", "SetGogo" };
        var code = new List<Avm1Instruction>();
        Avm1Instruction Op(byte opcode, Avm1Operand? operand = null, Avm1CodeBlock? body = null) =>
            new(opcode, code.Count, 1, [], operand, null, body);
        Avm1PushStringValue Str(int index) => new(8, (ushort)index);
        void Push(params Avm1PushValue[] values) => code.Add(Op(0x96, new Avm1PushOperand([.. values])));
        for (var i = 6; i < names.Length; i++)
        {
            code.Add(Op(0x9B, new Avm1FunctionOperand((ushort)i, 0, 0, [], 1),
                new Avm1CodeBlock(0, 1, [new(0, 0, 1, [], null, null, null)])));
            Push(Str(i)); code.Add(Op(0x1C));
            Push(new Avm1PushNullValue(), Str(i), new Avm1PushIntegerValue(3), Str(2));
            code.Add(Op(0x1C)); Push(Str(3)); code.Add(Op(0x4E));
            Push(Str(4)); code.Add(Op(0x4E)); Push(Str(5)); code.Add(Op(0x52)); code.Add(Op(0x17));
        }
        code.Add(Op(0));
        var action = new LmbAction(0, [], 0) { Code = new(0, code.Count, [.. code.Select((op, i) => op with { Offset = i })]) };
        sprite = sprite with { Timeline = [.. sprite.Timeline, new LmbDoActionCommand(0, 0, [], null!)] };
        var movie = new LmbMovieDefinition(null!, null, [.. names.Select((name, i) => new LmbString(i, name, 0))],
            [], [new(1, 0, 0, 1, 0, 0)], [], [], [], [action], [shape], [sprite], []);
        return new(new LumenPlayer(movie, 1280, 720, rootCharacterId: 1), LumenMatrix.Identity with { X = 400, Y = 250 }, 0, 0);
    }
}
