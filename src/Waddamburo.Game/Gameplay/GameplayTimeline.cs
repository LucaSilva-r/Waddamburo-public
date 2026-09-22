namespace Waddamburo.Game.Gameplay;

/// <summary>Maps a transport containing pre-roll silence to chart and song time.</summary>
public sealed class GameplayTimeline
{
    public GameplayTimeline(TimeSpan authoredOffset, TimeSpan leadIn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leadIn, TimeSpan.Zero);
        AuthoredOffset = authoredOffset;
        LeadIn = leadIn > -authoredOffset ? leadIn : -authoredOffset;
    }

    public TimeSpan AuthoredOffset { get; }
    public TimeSpan LeadIn { get; }
    // TJA positive OFFSET puts chart zero before audio zero.
    public TimeSpan AudioStart => LeadIn + AuthoredOffset;
    public TimeSpan ChartTime(TimeSpan transportPosition) => transportPosition - LeadIn;
}
