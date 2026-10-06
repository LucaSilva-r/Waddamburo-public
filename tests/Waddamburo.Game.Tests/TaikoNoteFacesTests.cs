using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoNoteFacesTests
{
    [Theory]
    [InlineData(0, "level01")]
    [InlineData(50, "level01")] // traced: 50 still sent level01
    [InlineData(51, "level02")]
    [InlineData(150, "level02")]
    [InlineData(151, "level03")]
    [InlineData(300, "level03")]
    [InlineData(301, "level04")]
    public void ComboPicksTheTracedTier(int combo, string label) => Assert.Equal(label, TaikoNoteFaces.Label(combo));

    [Fact]
    public void LongNotesPlayOnToTheBeatsPhase()
    {
        // A 20-frame level03 loop at frames 100..119 (40 frames a beat).
        Assert.Equal(0, TaikoNoteFaces.LoopSteps(100, 120, 100, 0)); // on phase: holds (a still review)
        Assert.Equal(5, TaikoNoteFaces.LoopSteps(100, 120, 100, 5 / 40d)); // a beat running on: plays on
        Assert.Equal(7, TaikoNoteFaces.LoopSteps(100, 120, 118, 5 / 40d)); // behind across the loop's end: wraps forward
        Assert.Equal(0, TaikoNoteFaces.LoopSteps(100, 120, 106, 5 / 40d)); // a frame ahead: waits (never plays the loop round)
        Assert.Equal(0, TaikoNoteFaces.LoopSteps(100, 120, 40, 0.3)); // not on this tier yet
    }
}
