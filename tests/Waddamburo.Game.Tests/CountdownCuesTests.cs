using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class CountdownCuesTests
{
    [Fact]
    public void PlaysTheTracedCuesOncePerSecondDownToZero()
    {
        var cues = new CountdownCues();
        var played = new List<(string, int)>();
        foreach (var second in new[] { 50, 50, 31, 30, 11, 10, 9, 9, 6, 5, 3, 0, 0 })
            played.AddRange(cues.Advance(second));

        Assert.Equal(1, played.Count(static cue => cue == ("VO_COM", 1)));
        Assert.Equal(1, played.Count(static cue => cue == ("VO_COM", 2)));
        Assert.Equal(1, played.Count(static cue => cue == ("VO_COM", 3)));
        Assert.Equal(11, played.Count(static cue => cue == ("SE_COM", 13))); // 10 .. 0, skipped seconds included
        Assert.Equal(("VO_COM", 2), played[played.IndexOf(("SE_COM", 13)) - 1]); // the voice, then the tick
    }

    [Fact]
    public void AFreshTimerStartsSilently()
    {
        var cues = new CountdownCues();
        Assert.Empty(cues.Advance(8));
        Assert.Equal([("SE_COM", 13)], cues.Advance(7));
    }
}
