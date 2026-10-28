using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class CategoryPreviewRevealTests
{
    [Fact]
    public void TextAppearsNearTheEndAndArtworkWaitsForTheFullSizeCard()
    {
        var reveal = new CategoryPreviewReveal();
        foreach (var amount in new[] { .2f, .5f, .8f })
        {
            reveal.Advance(amount);
            Assert.Equal(0, reveal.TextOpacity(1));
            Assert.Equal(0, reveal.ArtworkScale(1));
        }
        reveal.Advance(.9f);
        Assert.InRange(reveal.TextOpacity(1), .4f, .6f);
        Assert.Equal(0, reveal.ArtworkScale(1));
        reveal.Advance(1);
        Assert.Equal(1, reveal.TextOpacity(1));
        Assert.Equal(0, reveal.ArtworkScale(0));
        Assert.InRange(reveal.ArtworkScale(1), 0, .1f);
        var previous = reveal.ArtworkScale(1);
        for (var tick = 1; tick < CategoryPreviewReveal.ArtworkTicks; tick++)
        {
            reveal.Advance(1);
            Assert.InRange(reveal.ArtworkScale(.5f), previous, reveal.ArtworkScale(1));
            previous = reveal.ArtworkScale(1);
        }
        Assert.Equal(1, previous);
    }

    [Fact]
    public void OutgoingArtworkKeepsItsSizeAndTheNextOpeningStartsFromZero()
    {
        var reveal = new CategoryPreviewReveal();
        for (var tick = 0; tick < 20; tick++) reveal.Advance(1);
        reveal.Advance(.9f);
        Assert.Equal(1, reveal.ArtworkScale(.5f));
        reveal.Advance(.5f);
        Assert.Equal(1, reveal.ArtworkScale(1));
        reveal.Advance(0);
        reveal.Advance(.9f);
        Assert.Equal(0, reveal.ArtworkScale(1));
        reveal.Advance(1);
        Assert.Equal(0, reveal.ArtworkScale(0));
    }
}
