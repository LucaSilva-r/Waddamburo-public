using Waddamburo.Game.Scores;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

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

/// <summary>A carded player at the credit's end: their drum, medals before this credit and what it paid.</summary>
public sealed record RewardShopPlayer(int Side, int MedalsBefore, MedalCredit Credit);

/// <summary>A goods slot as the movie takes it; songs add their genre (1-8) and the four courses' stars.</summary>
public sealed record RewardShopGoods(ShopGoods Goods, int Genre = 0, int[]? Stars = null);

/// <summary>
/// A shopping visit's game side: the goods, the medals, buying (goods no → the server's answer; null: unreachable), the
/// preview (side, type, id → whether the Don's costume changes; -2 = the player's own) and the detail fills (fillrect id,
/// type, id → the surface: 6/7 the name, 8/9 the icon, 10/11 a song's title, for 1P/2P).
/// </summary>
public sealed record RewardShopVisit(
    IReadOnlyList<RewardShopGoods> Goods,
    int Medals,
    Func<int, Task<ShopPurchase?>> Purchase,
    Func<int, int, int, bool> Preview,
    Func<int, int, int, LumenNativeSurfaceKey?> Fill,
    Func<int, int, LumenNativeSurfaceKey?> BoxIcon);

/// <summary>
/// reward_shop.lm, Green's ごほうびタイム (traced research/traces/session-rewards-1.md). Medal mode (scene 12): the credit's Don
/// Medals with the daily, weekly and monthly bonuses, then game over; shop_gameover sits under it and waits (IsReady_Gameover
/// 0) until the screen ends: NotifyEndSequence, then the over_msg fade (2 s traced). Shopping mode (<paramref name="visit"/>,
/// scene 21, after a card entry): the season's goods for 100 s, bought with medals, then Song Select.
/// </summary>
public sealed class RewardShopHostBinding(int season, string telop, RewardShopPlayer player, IRewardShopSoundController? sounds = null,
    RewardShopVisit? visit = null, Don.IDonPresentationController? don = null)
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
    private int _answerIn = -1;
    private int _answerCode;
    private int _spent;
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
                Don.DonLumenBinding.RegisterMotion(context, lumen, don);
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
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ResumeTimer", _ =>
        {
            _timerPaused = false;
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
            _purchase = shop.Purchase(arg(call, 1));
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
        foreach (var (goods, genre, stars) in visit?.Goods ?? [])
        {
            LumenPlayerCalls.Call(lumenPlayer, "PushGoods", number(goods.No), number(goods.Type), number(goods.Id), number(goods.Price));
            var ownedP1 = goods.Owned && player.Side == 0;
            var ownedP2 = goods.Owned && player.Side == 1;
            LumenPlayerCalls.Call(lumenPlayer, "SetAcquiredGoods", number(goods.No), boolean(ownedP1), boolean(ownedP2));
            if (stars is [var easy, var normal, var hard, var oni, ..])
            {
                LumenPlayerCalls.Call(lumenPlayer, "SetGenreGoods", number(genre));
                LumenPlayerCalls.Call(lumenPlayer, "SetStarGoods", number(easy), number(normal), number(hard), number(oni));
            }
        }
        // ponytail: one carded player; the other drum shows empty.
        // Traced order: the other side first.
        var other = 1 - player.Side;
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(other), boolean(false), boolean(false));
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerEnter", number(player.Side), boolean(true), boolean(true));
        var credit = player.Credit;
        if (visit is null)
            LumenPlayerCalls.Call(lumenPlayer, "SetPlayerBonus", boolean(credit.Daily), boolean(credit.Weekly), boolean(credit.Monthly));
        // (earned, cap, spent, this credit): Waddamburo's wallet is already earned minus spent.
        LumenPlayerCalls.Call(lumenPlayer, "SetPlayerMedal", number(player.MedalsBefore + credit.Medals), number(DonMedals.Cap),
            number(0), number(credit.Medals));
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
        if (_timerTicks >= 0 && !_timerPaused)
            _timerTicks++;
        if (_costumeLoaded >= 0 && _movie is not null)
        {
            LumenPlayerCalls.Call(_movie, "FinishCostumeLoad", LumenHostValue.FromNumber(_costumeLoaded));
            _costumeLoaded = -1;
        }
        tickPurchase();
        if (visit is not null || _ticksSinceEnd < 0 || _ticksSinceEnd >= GameOverDelayTicks)
            return;
        // Its last frame stays up otherwise, over the game over under it.
        if (++_ticksSinceEnd == GameOverDelayTicks && _movie is not null)
            _movie.Hidden = true;
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
            var balance = visit!.Medals;
            LumenPlayerCalls.Call(_movie, "UpdateMedal", LumenHostValue.FromNumber(balance), LumenHostValue.FromNumber(_spent));
            if (answer is { Result: "ok" })
            {
                _spent = balance - answer.Medals;
                LumenPlayerCalls.Call(_movie, "UpdateMedal", LumenHostValue.FromNumber(balance), LumenHostValue.FromNumber(_spent));
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
