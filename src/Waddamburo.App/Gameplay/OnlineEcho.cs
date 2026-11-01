using System.Globalization;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// WADDAMBURO_ONLINE_ECHO=ms plays a song online against yourself: a second lane, fed your own hits through the same
/// relay and feed the other player's would go through, that many ms behind. For seeing the delayed lane without a partner.
/// </summary>
internal sealed class OnlineEcho
{
    private readonly HitRelay _local = new();
    private readonly HitRelay _remote = new();
    private readonly RemoteLaneFeed _feed;
    private readonly RemoteLaneFeed _unused = new();

    private OnlineEcho(TimeSpan delay) => _feed = new RemoteLaneFeed { Delay = delay };

    /// <summary>The echo's delay, when it's on.</summary>
    public static TimeSpan? Delay => Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_ECHO") is { Length: > 0 } text
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds) : null;

    /// <summary>
    /// Sets the gameplay up for it before a song starts; null, with any earlier echo cleared from <paramref name="clear"/>,
    /// when it's off or <paramref name="gameplay"/> is null (a song that isn't echoed).
    /// </summary>
    public static OnlineEcho? Start(TaikoGameplayPresentation? gameplay, TaikoGameplayPresentation? clear = null)
    {
        if ((gameplay ?? clear) is { } any)
        {
            any.RemoteLane = null;
            any.Relay = null;
        }
        if (gameplay is null || Delay is not { } delay)
            return null;
        var echo = new OnlineEcho(delay);
        gameplay.RemoteLane = echo._feed;
        gameplay.Relay = echo._local;
        return echo;
    }

    /// <summary>Passes what's been played to the second lane, as a network would (but losing nothing).</summary>
    public void Pump()
    {
        _remote.Read(_local.Build(), _feed);
        _local.Read(_remote.Build(), _unused);
    }
}
