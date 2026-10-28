using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class SongPreviewRevealTests
{
    [Fact]
    public void TitleFinishesBeforePauseAndDifficultyFadeWithFractionalFrames()
    {
        var reveal = new SongPreviewReveal();
        for (var tick = 0; tick < 8; tick++)
        {
            reveal.Advance(0, 1, true, true);
            Assert.Equal(0, reveal.DifficultyOpacity(1));
            Assert.True(reveal.TitleOpacity(0) <= reveal.TitleOpacity(.5f));
            Assert.True(reveal.TitleOpacity(.5f) <= reveal.TitleOpacity(1));
        }
        Assert.Equal(1, reveal.TitleOpacity(1));
        for (var tick = 0; tick < 4; tick++)
        {
            reveal.Advance(0, 1, true, true);
            Assert.Equal(1, reveal.TitleOpacity(1));
            Assert.Equal(0, reveal.DifficultyOpacity(1));
        }
        for (var tick = 0; tick < 8; tick++) reveal.Advance(0, 1, true, true);
        Assert.Equal(1, reveal.DifficultyOpacity(1));
    }

    [Fact]
    public void InterruptedOrContractedCardsCannotCarryAnOldRevealIntoTheNextSong()
    {
        var reveal = new SongPreviewReveal();
        for (var tick = 0; tick < 30; tick++) reveal.Advance(0, 1, true, true);
        reveal.Advance(0, 2, true, true);
        Assert.Equal(0, reveal.TitleOpacity(0));
        Assert.Equal(0, reveal.DifficultyOpacity(1));
        for (var tick = 0; tick < 30; tick++) reveal.Advance(0, 2, true, false);
        Assert.Equal(0, reveal.TitleOpacity(1));
        reveal.Advance(0, 2, true, true);
        Assert.InRange(reveal.TitleOpacity(.5f), .001f, .99f);
        reveal.Advance(0, 2, false, true);
        Assert.Equal(0, reveal.TitleOpacity(1));
        Assert.Equal(0, reveal.DifficultyOpacity(1));
    }
}
