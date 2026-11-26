using Waddamburo.Features.Shop;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Tests;

public sealed class ShopModuleTests
{
    [Fact]
    public async Task ADrumBuysFromItsOfferAndANewCreditClosesTheShop()
    {
        var credit = new CreditState { SongsPlayed = 2 };
        var shop = new ShopModule(credit);
        List<ShopGoods> goods = [new(1, 5, 117, 500, false), new(2, 4, 117, 500, true)];
        var medals = 900;
        shop.Offer(1, new(Task.FromResult(new ShopState(4, "Winter reward shop", medals, goods)),
            no => Task.FromResult(new ShopPurchase("ok", medals -= goods[no - 1].Price))));

        Assert.False(shop.Ready(0));
        Assert.Equal(900, shop.Medals(1));
        Assert.True(shop.Owns(1, 2));
        Assert.False(shop.Owns(1, 1));
        Assert.Null(await shop.Purchase(0, 1)); // no shop on that drum
        Assert.Equal(400, (await shop.Purchase(1, 1))!.Medals);

        credit.SongsPlayed = 0;
        Assert.False(shop.Ready(1));
        Assert.Empty(shop.WaitForGoods().Goods);
    }
}
