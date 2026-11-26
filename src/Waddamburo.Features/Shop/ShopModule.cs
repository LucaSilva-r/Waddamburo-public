using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Shop;

/// <summary>A drum's reward shop: this season's goods and the card's medals, and buying goods by number.</summary>
public sealed record ShopOffer(Task<ShopState> State, Func<int, Task<ShopPurchase>> Purchase);

/// <summary>
/// The reward shop: each drum's card's goods and medals for the credit (fetched at the card read), and buying.
/// Frontends only show it: Green's reward_shop binding and our own movies call the same verbs.
/// </summary>
public sealed class ShopModule
{
    private readonly ShopOffer?[] _offers = new ShopOffer?[2];

    /// <param name="credit">A new credit closes every drum's shop.</param>
    public ShopModule(CreditState credit) => credit.Started += Close;

    /// <summary>
    /// Asks TaikOnline for a drum's shop in the background (a card read, or an account chosen in the home player setup).
    /// A server without the shop answers 404: the fetch fails and the drum has no shop.
    /// </summary>
    public void Fetch(int side, ScoreClient client, long? baid)
    {
        var goods = Task.Run(() => client.ShopAsync(baid));
        _ = goods.ContinueWith(task => Console.WriteLine(task.IsCompletedSuccessfully
            ? $"Reward shop for side {side}: {task.Result.Goods.Count} goods."
            : $"Reward shop for side {side} unavailable: {task.Exception?.GetBaseException().Message}"), TaskScheduler.Default);
        Offer(side, new(goods, no => client.PurchaseAsync(no, baid, goods.Result.Goods.FirstOrDefault(item => item.No == no))));
    }

    /// <summary>Gives a drum a shop as it is (diagnostic start scenes serve a local one).</summary>
    public void Offer(int side, ShopOffer offer) => _offers[side] = offer;

    /// <summary>Every drum's shop goes (a new credit; one visit a credit).</summary>
    public void Close() => Array.Clear(_offers);

    /// <summary>Whether a drum's shop came in: its card buys with its medals; anyone else only looks.</summary>
    public bool Ready(int side) => _offers[side]?.State.IsCompletedSuccessfully == true;

    /// <summary>A drum's medals, once its shop is in.</summary>
    public int? Medals(int side) => Ready(side) ? _offers[side]!.State.Result.Medals : null;

    /// <summary>Whether a drum's card already owns goods number <paramref name="no"/>.</summary>
    public bool Owns(int side, int no) => Ready(side) && _offers[side]!.State.Result.Goods.Any(goods => goods.No == no && goods.Owned);

    /// <summary>The season's goods (the same for every card), once any drum's shop is in.</summary>
    public ShopState? Goods => _offers.FirstOrDefault(static offer => offer?.State.IsCompletedSuccessfully == true)?.State.Result;

    /// <summary>
    /// The goods for the shop scene: the mode select offers the shop before a card's fetch is in, so this waits for one
    /// still running (up to 10 s, the scene is loading anyway). ponytail: none came in (the fetch failed): an empty
    /// shop to look at, not an error message.
    /// </summary>
    public ShopState WaitForGoods()
    {
        var pending = _offers.Where(static offer => offer is not null && !offer.State.IsCompleted).Select(static offer => (Task)offer!.State).ToArray();
        if (Goods is null && pending.Length > 0)
            Task.WaitAny(pending, TimeSpan.FromSeconds(10));
        return Goods ?? new ShopState(0, "", 0, []);
    }

    /// <summary>
    /// A drum buys goods number <paramref name="no"/>: the server's answer, and the bought item unlocked on the player's
    /// profile. Null: the drum has no shop or the purchase failed (logged).
    /// </summary>
    public async Task<ShopPurchase?> Purchase(int side, int no)
    {
        if (!Ready(side))
            return null;
        try
        {
            var answer = await _offers[side]!.Purchase(no).ConfigureAwait(false);
            if (answer.Result == "ok" && Goods?.Goods.FirstOrDefault(goods => goods.No == no) is { } bought
                && TaikoGuest.Profiles[side] is { Locked: { } locks } profile)
                TaikoGuest.Profiles[side] = profile with { Locked = locks.Unlock(bought.Type, bought.Id, bought.MusicId) };
            return answer;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Warning SHOP: the purchase failed ({exception.Message}).");
            return null;
        }
    }
}
