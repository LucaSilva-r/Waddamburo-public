using Waddamburo.Game.Scores;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum RewardShopSoundRequestKind { Effect, Voice }

public readonly record struct RewardShopSoundRequest(RewardShopSoundRequestKind Kind, int Group, int Cue);

public interface IRewardShopSoundController
{
    void RequestSound(RewardShopSoundRequest request);
}

/// <summary>A carded player at the credit's end: their drum, medals before this credit and what it paid.</summary>
public sealed record RewardShopPlayer(int Side, int MedalsBefore, MedalCredit Credit);

/// <summary>
/// reward_shop.lm in medal mode (Green's ごほうびタイム; traced research/traces/session-rewards-1.md scene 12): the
/// credit's Don Medals with the daily, weekly and monthly bonuses, then game over. shop_gameover sits under it
/// and waits (IsReady_Gameover 0) until the screen ends: NotifyEndSequence, then the over_msg fade (2 s traced).
/// </summary>
public sealed class RewardShopHostBinding(int season, string telop, RewardShopPlayer player, IRewardShopSoundController? sounds = null)
    : ILumenHostBinding
{
    // Traced: IsReady_Gameover turns 1 about 2 s after NotifyEndSequence (over_msg's TweenVisible in and out).
    private const int GameOverDelayTicks = 120;
    private int _ticksSinceEnd = -1;

    /// <summary>When shop_gameover may start.</summary>
    public bool Ended => _ticksSinceEnd >= GameOverDelayTicks;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("IsReady", static _ => LumenHostValue.FromNumber(1));
            lumen.RegisterMethod("RequestSE", call => requestSound(RewardShopSoundRequestKind.Effect, call));
            lumen.RegisterMethod("RequestVO", call => requestSound(RewardShopSoundRequestKind.Voice, call));
            lumen.RegisterMethod("NotifyEndSequence", _ =>
            {
                _ticksSinceEnd = 0;
                return LumenHostValue.Undefined;
            });
            // Called by the movie to fill the indicator's name boards; Green answers nothing to it.
            lumen.RegisterMethod("SetPlayerBasicData", static _ => LumenHostValue.Undefined);
        });
    }

    /// <summary>The traced opening: mode 0 (medals), the season, the players and their medals.</summary>
    public void Attach(LumenPlayer lumenPlayer)
    {
        ArgumentNullException.ThrowIfNull(lumenPlayer);
        static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
        LumenPlayerCalls.Call(lumenPlayer, "Settings", number(0), number(season));
        LumenPlayerCalls.Call(lumenPlayer, "Telop", LumenHostValue.FromString(telop));
        // ponytail: no goods yet (medal mode never shows them); the shop API brings the season's.
        // Traced order: the other side first.
        var other = 1 - player.Side;
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(other), LumenHostValue.FromBoolean(false), LumenHostValue.FromBoolean(false));
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(player.Side), LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(true));
        var credit = player.Credit;
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerBonus", LumenHostValue.FromBoolean(credit.Daily),
            LumenHostValue.FromBoolean(credit.Weekly), LumenHostValue.FromBoolean(credit.Monthly));
        // (earned, cap, spent, this credit): Waddamburo's wallet is already earned minus spent.
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerMedal", number(player.MedalsBefore + credit.Medals), number(DonMedals.Cap),
            number(0), number(credit.Medals));
    }

    public void Tick()
    {
        if (_ticksSinceEnd >= 0 && _ticksSinceEnd < GameOverDelayTicks)
            _ticksSinceEnd++;
    }

    private LumenHostValue requestSound(RewardShopSoundRequestKind kind, LumenHostCall call)
    {
        if (sounds is not null && call.Arguments.Length >= 2
            && call.Arguments[0].Kind == LumenHostValueKind.Number && call.Arguments[1].Kind == LumenHostValueKind.Number)
            sounds.RequestSound(new RewardShopSoundRequest(kind, (int)call.Arguments[0].AsNumber(), (int)call.Arguments[1].AsNumber()));
        return LumenHostValue.Undefined;
    }
}
