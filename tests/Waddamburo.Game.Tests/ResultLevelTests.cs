using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class ResultLevelTests
{
    [Theory]
    // Oni's clear line is 40 of 50 segments: a fail from 20 up is おしかった (ALMOST).
    [InlineData(TaikoCourse.Oni, 19, false, 0)]
    [InlineData(TaikoCourse.Oni, 20, false, 1)]
    [InlineData(TaikoCourse.Easy, 15, false, 1)] // Easy's line is 30
    [InlineData(TaikoCourse.Easy, 14, false, 0)]
    [InlineData(TaikoCourse.Hard, 40, true, 2)]
    [InlineData(TaikoCourse.Hard, 50, true, 3)]
    public void FailAtHalfTheClearLineIsAlmost(TaikoCourse course, int segments, bool cleared, int level) =>
        Assert.Equal(level, new TaikoPlayResult(course, 0, 0, 0, 0, 0, 0, segments, cleared).ResultLevel);
}
