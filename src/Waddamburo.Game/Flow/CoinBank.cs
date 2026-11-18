namespace Waddamburo.Game.Flow;

/// <summary>
/// The cabinet's credit counter: a coin is credited the moment it arrives (its sound is queued
/// separately by the host); credits only go when a player joins. It lives for the whole process:
/// nothing but a restart resets it.
/// </summary>
public sealed class CoinBank(ArcadeSettings settings)
{
    public ArcadeSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public int Credits { get; private set; }

    public void InsertCoin() => Credits += Settings.CreditsPerCoin;

    /// <summary>The credits are service credits (the counter shows its service text) until they are used up.</summary>
    public bool Service { get; private set; }

    /// <summary>
    /// The service switch (traced session-service-coin): one more service credit; the first one replaces whatever
    /// coins were inserted (1 coin, then service: 1 service credit). It isn't a coin: the bookkeeping doesn't count it.
    /// </summary>
    public void ServiceCredit()
    {
        if (!Service)
            (Credits, Service) = (0, true);
        Credits++;
    }

    /// <summary>The credits inserted are dropped (the service menu opening).</summary>
    public void Clear() => (Credits, Service) = (0, false);

    /// <summary>What the next player to join pays: one player's price, or the rest of the two-player price.</summary>
    public int JoinCost(int joinedPlayers) => joinedPlayers == 0
        ? Settings.CreditsOnePlayer
        : Settings.CreditsTwoPlayers - Settings.CreditsOnePlayer;

    /// <summary>Credits still missing for the next join (0 when affordable).</summary>
    public int Missing(int joinedPlayers) => Math.Max(0, JoinCost(joinedPlayers) - Credits);

    public bool TryJoin(int joinedPlayers)
    {
        var cost = JoinCost(joinedPlayers);
        if (Credits < cost)
            return false;
        Credits -= cost;
        if (Credits == 0)
            Service = false;
        return true;
    }
}
