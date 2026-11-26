using Waddamburo.Game.Flow;
using Waddamburo.Skins.Green;

namespace Waddamburo.App.Flow.Credit;

/// <summary>The credit's state (<see cref="CreditState"/>, what every skin reads) and what only Green's screens use.</summary>
internal sealed class CreditSession : CreditState
{
    /// <summary>The carded player whose Don Medals the credit's end shows (reward_shop over the game over); null: none.</summary>
    // ponytail: a Green type in the app's credit; moves behind the skin with the reward screens.
    public RewardShopPlayer? RewardPlayer { get; set; }
}
