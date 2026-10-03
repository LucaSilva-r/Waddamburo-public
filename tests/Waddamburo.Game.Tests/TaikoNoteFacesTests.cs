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
}
