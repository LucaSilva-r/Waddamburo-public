using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class TaikoHitFlightsTests
{
    [Theory]
    [InlineData(PlayableLongNoteKind.Roll, TaikoInputAction.LeftDon, "don_hit")]
    [InlineData(PlayableLongNoteKind.Roll, TaikoInputAction.RightKa, "katsu_hit")]
    [InlineData(PlayableLongNoteKind.BigRoll, TaikoInputAction.RightDon, "don_renda_d_hit")]
    [InlineData(PlayableLongNoteKind.BigRoll, TaikoInputAction.LeftKa, "katsu_renda_d_hit")]
    [InlineData(PlayableLongNoteKind.Balloon, TaikoInputAction.LeftDon, "don_hit")]
    public void LongHitsSelectSurfaceAndSize(PlayableLongNoteKind kind, TaikoInputAction action, string expected)
    {
        Assert.Equal(expected, TaikoHitFlights.StateFor(kind, action));
    }

    [Fact]
    public void FlightsOverlapStayBoundedAndRetireWhenAuthoredTimelineStops()
    {
        var first = createLayer();
        var second = createLayer();
        var flights = new TaikoHitFlights([first, second]);
        var hit = new TaikoNoteJudgement(0, new PlayableHitObject(TimeSpan.Zero, PlayableNoteKind.Don),
            TaikoHitResult.Great, TimeSpan.Zero, false);
        flights.Trigger(hit);
        flights.Advance();
        flights.Trigger(hit);
        Assert.Equal(2, flights.ActiveLayers.Count());
        Assert.Equal(1, first.Player.CurrentFrame);
        Assert.Equal(0, second.Player.CurrentFrame);
        flights.Trigger(hit);
        Assert.Equal(2, flights.ActiveLayers.Count());
        flights.Advance();
        flights.Advance();
        Assert.Empty(flights.ActiveLayers);
        flights.Trigger(hit);
        Assert.Single(flights.ActiveLayers);
    }

    private static LumenSceneLayer createLayer()
    {
        var action = new LmbAction(0, [0x07, 0x00], 0)
        {
            Code = new Avm1CodeBlock(0, 2, [
                new Avm1Instruction(0x07, 0, 1, [], null, null, null),
                new Avm1Instruction(0x00, 1, 1, [], null, null, null),
            ]),
        };
        var sprite = new LmbSpriteDefinition(1, 1, 3, 1, 0, [], [
            new LmbFrameLabelCommand(0, 0, 0, [], null!),
            new LmbShowFrameCommand(0, 0, [], null!),
            new LmbShowFrameCommand(1, 0, [], null!),
            new LmbShowFrameCommand(2, 1, [], null!),
            new LmbDoActionCommand(0, 0, [], null!),
        ], null!);
        var movie = new LmbMovieDefinition(null!, null, [new LmbString(0, "don_hit", 0)],
            [], [], [], [], [], [action], [], [sprite], []);
        return new LumenSceneLayer(new LumenPlayer(movie, 1280, 720, rootCharacterId: 1),
            LumenMatrix.Identity, 0, 0);
    }

    [Theory]
    [InlineData(PlayableNoteKind.Don, "don_hit")]
    [InlineData(PlayableNoteKind.Ka, "katsu_hit")]
    [InlineData(PlayableNoteKind.BigDon, "don_d_hit")]
    [InlineData(PlayableNoteKind.BigKa, "katsu_d_hit")]
    public void SuccessfulJudgementSelectsAuthoredFlight(PlayableNoteKind kind, string label)
    {
        Assert.Equal(label, TaikoHitFlights.StateFor(kind, TaikoHitResult.Great, false));
        Assert.Equal(label, TaikoHitFlights.StateFor(kind, TaikoHitResult.Good, false));
        Assert.Null(TaikoHitFlights.StateFor(kind, TaikoHitResult.Miss, false));
        Assert.Null(TaikoHitFlights.StateFor(kind, null, false));
        Assert.Null(TaikoHitFlights.StateFor(kind, TaikoHitResult.Great, true));
    }
}
