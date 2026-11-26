using Waddamburo.Features.Rewards;
using Waddamburo.Game.Scores;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green;

public enum RewardShopSoundRequestKind { Effect, Voice }

public readonly record struct RewardShopSoundRequest(RewardShopSoundRequestKind Kind, int Group, int Cue);

public interface IRewardShopSoundController
{
    void RequestSound(RewardShopSoundRequest request);

    /// <summary>The medal screen's music (traced JINGLE_SOGO2, looping until the game over's replaces it).</summary>
    void StartMedalMusic();

    /// <summary>The shop's music (traced JINGLE_GOHOBI, from the scene's load; it comes back when a costume is previewed).</summary>
    void StartShopMusic() { }

    /// <summary>A purchase went through: the game's own cues (traced SE_GOHOBI 12 and VO_GOHOBI 6 with the medals' update).</summary>
    void Bought() { }

    /// <summary>The server's answer closes the purchase dialog (traced SE_COM 29 with ResponseServer).</summary>
    void Answered() { }

    void StopMusic() { }
}

/// <summary>A goods slot as the movie takes it: owned per side (1P, 2P); songs add their genre (1-8) and the four courses' stars.</summary>
public sealed record RewardShopGoods(ShopGoods Goods, bool[] Owned, int Genre = 0, int[]? Stars = null);

/// <summary>
/// A shopping visit's game side: the goods, the drums joined and each one's medals (null: a guest, who can't buy), buying
/// (side, goods no → the server's answer; null: unreachable), the preview (side, type, id → whether that Don's costume changes;
/// -2 = the player's own) and the detail fills (fillrect id, type, id → the surface: 6/7 the name, 8/9 the icon, 10/11 a song's
/// title, for 1P/2P).
/// </summary>
public sealed record RewardShopVisit(
    IReadOnlyList<RewardShopGoods> Goods,
    IReadOnlyList<int> Joined,
    int?[] Medals,
    Func<int, int, Task<ShopPurchase?>> Purchase,
    Func<int, int, int, bool> Preview,
    Func<int, int, int, LumenNativeSurfaceKey?> Fill,
    Func<int, int, LumenNativeSurfaceKey?> BoxIcon)
{
    /// <summary>A purchase shows over_msg's "connecting" (arcade, for authenticity; home skips the wait it suggests).</summary>
    public bool ServerMessages { get; init; }
}

/// <summary>
/// reward_shop.lm, Green's ごほうびタイム (traced research/traces/session-rewards-1.md). Medal mode (scene 12): the credit's Don
/// Medals with the daily, weekly and monthly bonuses, then game over; shop_gameover sits under it and waits (IsReady_Gameover
/// 0) until the screen ends: NotifyEndSequence, then the over_msg fade (2 s traced). Shopping mode (<paramref name="visit"/>,
/// scene 21, after a card entry): the season's goods for 100 s, bought with medals, then Song Select. One of
/// <paramref name="player"/> (medal mode) and <paramref name="visit"/> (shopping) is given.
/// </summary>
public sealed class RewardShopHostBinding(int season, string telop, MedalScreen? player, IRewardShopSoundController? sounds = null,
    RewardShopVisit? visit = null, Waddamburo.Skins.Green.Don.IDonPresentationController? don = null, IndicatorParts? parts = null)
    : ILumenHostBinding
{
    // Traced: IsReady_Gameover turns 1 about 2 s after NotifyEndSequence (over_msg's TweenVisible in and out).
    private const int GameOverDelayTicks = 120;
    private const int ShopSeconds = 100;
    // Traced: the server's answer closes the purchase about 3.7 s after the medals' update (the "got it" voice).
    private const int AnswerDelayTicks = 220;
    private int _ticksSinceEnd = -1;
    private LumenPlayer? _movie;
    private int _timerTicks = -1;
    private bool _timerPaused;
    private Task<ShopPurchase?>? _purchase;
    private int _purchaseSide;
    private int _answerIn = -1;
    private int _answerCode;
    private bool _counterStarted;
    private bool _namesShown;
    private readonly int[] _spent = new int[2];
    // The Don wears a previewed costume: FinishCostumeLoad on the next tick (the 3D Don dresses at once).
    private int _costumeLoaded = -1;

    /// <summary>When shop_gameover may start (medal mode) or Song Select (shopping).</summary>
    public bool Ended => _ticksSinceEnd >= (visit is null ? GameOverDelayTicks : 0);

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
            if (visit is not null)
                installShop(lumen, visit);
            // After a purchase: SetMotion(side, DON_ENTRY1P_COSCH_OUT, DON_GENERAL_LOOP) (traced).
            if (don is not null)
                Screens.DonLumenBinding.RegisterMotion(context, lumen, don);
        });
    }

    private void installShop(LumenHostObject lumen, RewardShopVisit shop)
    {
        static int arg(LumenHostCall call, int index) =>
            index < call.Arguments.Length && call.Arguments[index].Kind == LumenHostValueKind.Number ? (int)call.Arguments[index].AsNumber() : 0;
        lumen.RegisterMethod("StartTimer", _ =>
        {
            _timerTicks = 0;
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("PauseTimer", _ =>
        {
            _timerPaused = true;
            parts?.PauseCountdown();
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ResumeTimer", _ =>
        {
            _timerPaused = false;
            parts?.ResumeCountdown();
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("GetSecTimer", _ => LumenHostValue.FromNumber(secondsLeft()));
        lumen.RegisterMethod("IsTimeup", _ => LumenHostValue.FromBoolean(_timerTicks >= 0 && secondsLeft() <= 0));
        lumen.RegisterMethod("UpdateFillrect", call =>
        {
            var (slot, type, id) = (arg(call, 0), arg(call, 1), arg(call, 2));
            if (_movie is not null && fillName(slot) is { } name && shop.Fill(slot, type, id) is { } surface)
                _movie.SetNativeFill(name, surface);
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("UpdatePreview", call =>
        {
            var (side, type, id) = (arg(call, 0), arg(call, 1), arg(call, 2));
            if (type is 1 or 2)
                sounds?.StopMusic(); // a song (or tone) plays its own preview
            else
                sounds?.StartShopMusic();
            var changes = shop.Preview(side, type, id);
            if (changes)
                _costumeLoaded = side;
            return LumenHostValue.FromBoolean(changes);
        });
        lumen.RegisterMethod("SendShopServer", call =>
        {
            _purchaseSide = arg(call, 0) == 1 ? 1 : 0;
            _purchase = shop.Purchase(_purchaseSide, arg(call, 1));
            if (shop.ServerMessages)
                parts?.ShowMessage(0);
            return LumenHostValue.Undefined;
        });
        // Traced calls the game answers with nothing visible here.
        foreach (var name in new[] { "ShopStart", "GetPlayerInfo_Gameover", "AllStopSoundCheck", "Apply", "SelectCommonSound", "NotifyChangeCostumeEffect" })
            lumen.RegisterMethod(name, static _ => LumenHostValue.Undefined);
        lumen.RegisterMethod("IsReady_Gameover", static _ => LumenHostValue.FromBoolean(false));
    }

    // Resource.FILLRECT_*: 6/7 the detail's name, 8/9 its icon, 10/11 a song's title (1P/2P). The movie attaches the
    // icon as detail_icon_<n>p and the name plates as detail_name_<n>p / detail_songname_<n>p.
    // ponytail: names read from the movie's script; checked on screen.
    private static string? fillName(int slot) => slot switch
    {
        6 or 7 => $"detail_name_{slot - 5}p",
        8 or 9 => $"detail_icon_{slot - 7}p",
        10 or 11 => $"detail_songname_{slot - 9}p",
        _ => null,
    };

    private int secondsLeft() => Math.Max(0, ShopSeconds - (_timerTicks < 0 ? 0 : _timerTicks / 60));

    /// <summary>The traced opening: the mode, the season, the goods, the players and their medals.</summary>
    public void Attach(LumenPlayer lumenPlayer)
    {
        ArgumentNullException.ThrowIfNull(lumenPlayer);
        _movie = lumenPlayer;
        if (visit is null)
            sounds?.StartMedalMusic();
        else
            sounds?.StartShopMusic();
        static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
        static LumenHostValue boolean(bool value) => LumenHostValue.FromBoolean(value);
        LumenPlayerCalls.Call(lumenPlayer, "Settings", number(visit is null ? 0 : 1), number(season));
        LumenPlayerCalls.Call(lumenPlayer, "Telop", LumenHostValue.FromString(telop));
        // Traced per slot: PushGoods, SetAcquiredGoods, and a song's genre and stars.
        foreach (var (goods, owned, genre, stars) in visit?.Goods ?? [])
        {
            LumenPlayerCalls.Call(lumenPlayer, "PushGoods", number(goods.No), number(goods.Type), number(goods.Id), number(goods.Price));
            LumenPlayerCalls.Call(lumenPlayer, "SetAcquiredGoods", number(goods.No), boolean(owned[0]), boolean(owned[1]));
            if (stars is [var easy, var normal, var hard, var oni, ..])
            {
                LumenPlayerCalls.Call(lumenPlayer, "SetGenreGoods", number(genre));
                LumenPlayerCalls.Call(lumenPlayer, "SetStarGoods", number(easy), number(normal), number(hard), number(oni));
            }
        }
        // SetPlayerEnter(side, joined, carded) picks the player the following SetPlayerBonus/SetPlayerMedal are for (the
        // movie's tmp_pid). Traced order: an empty drum first.
        if (player is not null)
        {
            LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(1 - player.Side), boolean(false), boolean(false));
            LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(player.Side), boolean(true), boolean(true));
            var credit = player.Credit;
            LumenPlayerCalls.Call(lumenPlayer, "SetPlayerBonus", boolean(credit.Daily), boolean(credit.Weekly), boolean(credit.Monthly));
            // (earned, cap, spent, this credit): Waddamburo's wallet is already earned minus spent.
            LumenPlayerCalls.Call(lumenPlayer, "SetPlayerMedal", number(player.MedalsBefore + credit.Medals), number(DonMedals.Cap),
                number(0), number(credit.Medals));
            foreach (var reward in player.Rewards ?? [])
                LumenPlayerCalls.Call(lumenPlayer, "PushReward", number(reward.Type), number(reward.Id));
        }
        else if (visit is not null)
            foreach (var side in Enumerable.Range(0, 2).OrderBy(side => visit.Joined.Contains(side)))
            {
                var medals = visit.Medals[side];
                LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(side), boolean(visit.Joined.Contains(side)), boolean(medals is not null));
                if (visit.Joined.Contains(side) && medals is { } wallet)
                    LumenPlayerCalls.Call(lumenPlayer, "SetPlayerMedal", number(wallet), number(DonMedals.Cap), number(0), number(0));
            }
        // The goods boxes' icons (CostumeIconFillrect clips attached as goods_icon_<box>): box 0 is the exit, costumes from
        // box 1, songs and tones from box 12 (the movie's UpdateLineup). The game draws them without being asked.
        if (visit is not null)
        {
            var costumes = visit.Goods.Where(static entry => entry.Goods.Type is not (1 or 2)).ToList();
            var songs = visit.Goods.Where(static entry => entry.Goods.Type is 1 or 2).ToList();
            for (var index = 0; index < costumes.Count; index++)
                if (visit.BoxIcon(costumes[index].Goods.Type, costumes[index].Goods.Id) is { } icon)
                    lumenPlayer.SetNativeFill($"goods_icon_{1 + index}", icon);
            for (var index = 0; index < songs.Count; index++)
                if (visit.BoxIcon(songs[index].Goods.Type, songs[index].Goods.Id) is { } icon)
                    lumenPlayer.SetNativeFill($"goods_icon_{12 + index}", icon);
        }
    }

    public void Tick()
    {
        // The counter starts with the shop's timer, once its movie is set up (it can come after StartTimer).
        // The name boards as soon as their movies are set up (traced: filled and faded in before Settings).
        if (parts is not null && !_namesShown && parts.PollReady())
        {
            _namesShown = true;
            parts.ShowShopNames(visit?.Joined ?? (player is { } medals ? [medals.Side] : []));
        }
        if (parts is not null && parts.PollReady() && _timerTicks >= 0 && !_counterStarted)
        {
            _counterStarted = true;
            parts.StartCountdown(secondsLeft());
        }
        // A frozen timer (the operator's setting) never runs out.
        if (_timerTicks >= 0 && !_timerPaused && parts?.Countdown != false)
            _timerTicks++;
        if (_costumeLoaded >= 0 && _movie is not null)
        {
            LumenPlayerCalls.Call(_movie, "FinishCostumeLoad", LumenHostValue.FromNumber(_costumeLoaded));
            _costumeLoaded = -1;
        }
        tickPurchase();
        // Its last frame stays up: the game over's curtain slides over it.
        if (visit is null && _ticksSinceEnd >= 0 && _ticksSinceEnd < GameOverDelayTicks)
            _ticksSinceEnd++;
    }

    // Traced: UpdateMedal(balance, spent so far) as the request goes, UpdateMedal(balance, spent with it) when it went through,
    // then ResponseServer(0) once the dialog's voice is over. The balance stays the opening one; the movie shows balance - spent.
    private void tickPurchase()
    {
        if (_movie is null)
            return;
        if (_purchase is { IsCompleted: true } purchase)
        {
            _purchase = null;
            var answer = purchase.IsCompletedSuccessfully ? purchase.Result : null;
            // The buyer's (the movie's sendPlayerID, set by SendShopServer).
            var balance = visit!.Medals[_purchaseSide] ?? 0;
            ref var spent = ref _spent[_purchaseSide];
            LumenPlayerCalls.Call(_movie, "UpdateMedal", LumenHostValue.FromNumber(balance), LumenHostValue.FromNumber(spent));
            if (answer is { Result: "ok" })
            {
                spent = balance - answer.Medals;
                LumenPlayerCalls.Call(_movie, "UpdateMedal", LumenHostValue.FromNumber(balance), LumenHostValue.FromNumber(spent));
                if (visit.ServerMessages)
                    parts?.ShowMessage(27);
                sounds?.Bought();
                _answerIn = AnswerDelayTicks;
                _answerCode = 0;
            }
            else
            {
                // ponytail: RESPONSECODE_NG = 1 assumed (the movie's constants: OK 0, NG, OFFLINE).
                _answerIn = 1;
                _answerCode = 1;
            }
        }
        if (_answerIn > 0 && --_answerIn == 0)
        {
            sounds?.Answered();
            if (visit?.ServerMessages == true)
                parts?.HideCardBand();
            LumenPlayerCalls.Call(_movie, "ResponseServer", LumenHostValue.FromNumber(_answerCode));
        }
    }

    private LumenHostValue requestSound(RewardShopSoundRequestKind kind, LumenHostCall call)
    {
        if (sounds is not null && call.Arguments.Length >= 2
            && call.Arguments[0].Kind == LumenHostValueKind.Number && call.Arguments[1].Kind == LumenHostValueKind.Number)
            sounds.RequestSound(new RewardShopSoundRequest(kind, (int)call.Arguments[0].AsNumber(), (int)call.Arguments[1].AsNumber()));
        return LumenHostValue.Undefined;
    }
}
