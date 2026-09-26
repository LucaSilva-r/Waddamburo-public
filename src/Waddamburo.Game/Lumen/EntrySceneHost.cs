using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// Player entry as the game drives it (traced free-play guest flow, research/traces): the entry
/// movie's host methods, its countdown (shown by the indicator parts), and what the game does when
/// a player joins. One instance per loaded entry scene.
/// </summary>
public sealed class EntrySceneHost(IndicatorParts parts)
{
    private const int EntrySeconds = 50;

    private readonly System.Diagnostics.Stopwatch _timer = new();
    private TimeSpan _timerLimit;
    private bool _timerStarted;
    private LumenPlayer? _entry;
    private readonly HashSet<int> _joined = [];

    public IndicatorParts Parts { get; } = parts ?? throw new ArgumentNullException(nameof(parts));

    /// <summary>A player joined (the game switches the coin panel to its split state here).</summary>
    public event Action<int>? PlayerJoined;

    /// <summary>Costume picker icon (name: false) or name plate (true) for (ICONTYPE 0 costume / 1 head / 2 body, id).</summary>
    public Func<int, int, bool, LumenNativeSurfaceKey?>? CostumeIcon { get; init; }

    /// <summary>Whether a player may join, on either drum (the right one is P2); both may.</summary>
    public Func<int, bool> CanJoin { get; init; } = static _ => true;

    /// <summary>The first joined player's drum (0 left, 1 right); null before a join.</summary>
    public int? JoinedPlayer => _joined.Count == 0 ? null : _joined.First();

    /// <summary>The cabinet's credits in coin mode; null in free play.</summary>
    public CoinBank? Coins { get; init; }

    /// <summary>Plays a VO_ENTRY cue the game itself requests (join and coin-mode prompts).</summary>
    public Action<int>? PlayVoice { get; init; }

    /// <summary>Plays a cue the game itself plays (countdown ticks and voices, the card band).</summary>
    public Action<string, int>? PlayCue { get; init; }

    // The entry countdown and the card dialog's own 20 s count (the movie shows it; the game plays its cues).
    private const int CardDialogSeconds = 20;
    private readonly CountdownCues _timerCues = new(), _dialogCues = new();
    private int _dialogAt = -1;

    private void play(IEnumerable<(string Bank, int Cue)> cues)
    {
        foreach (var (bank, cue) in cues)
            PlayCue?.Invoke(bank, cue);
    }

    /// <summary>
    /// A card the server rejected: the red band (error_reading_ng) with the card error sound (SE_COM 12,
    /// SE_COM_COM_CARD_ERROR), for 2 s. ponytail: the game's handling of an unregistered card is untraced.
    /// </summary>
    public void RejectCard()
    {
        if (_entry is null)
        {
            _rejectOnLoad = true;
            return;
        }
        Parts.ShowCardBand(IndicatorParts.CardBand.ReadNg);
        PlayCue?.Invoke("SE_COM", 12);
        _rejectUntil = _ticks + 120;
    }

    /// <summary>
    /// Leave the entry for the attract loop: a rejected card with nobody joined and no credits would
    /// otherwise leave it waiting for coins (user-requested).
    /// </summary>
    public Action? ReturnToAttract { get; init; }

    private bool _rejectOnLoad;
    private int _rejectUntil = -1;

    /// <summary>A card being read or waiting for a drum (from the attract loop or read during entry).</summary>
    public ScoreProfile? Card { get; set; }

    /// <summary>
    /// A card read while the entry runs (traced session14-card-entry): InputCardReader at once, then the
    /// same sequence as one read in the attract loop. One card at a time; false while one is pending.
    /// </summary>
    public bool InsertCard(ScoreProfile card)
    {
        if (Card is not null || _entry is not { } entry)
            return false;
        Card = card;
        call(entry, "InputCardReader");
        return true;
    }

    /// <summary>EntryData(side): the drum that took the card.</summary>
    public Action<int, ScoreProfile>? CardClaimed { get; init; }

    /// <summary>A card is being read (true) until a drum takes it or the dialog closes (false).</summary>
    public Action<bool>? CardDialog { get; init; }

    /// <summary>The look of the player about to join a drum (a card or the home account); null: a guest.</summary>
    public Func<int, DonLook?>? PlayerLook { get; init; }

    /// <summary>The dialog's Don (slot 2) and the players' costume-loaded Dons.</summary>
    public IDonPresentationController? Don { get; init; }

    // Traced after Wait_AccessServer: the dialog Don's costume loads at 0.3 s, the band turns green at
    // 0.7 s, ReplyServer(true) at 1.1-1.3 s (the player data round trip).
    private const int DialogDonTicks = 18, BandOkTicks = 42, ServerReplyTicks = 68;
    private int _cardReadAt = -1;

    private const int PromptDelayTicks = 50;     // traced 0.83 s after entry loads
    private const int PromptRepeatTicks = 300;   // traced 5.0 s between "insert coins" prompts
    private int _ticks;

    /// <summary>
    /// Per-tick coin-mode prompts before anyone joins: VO_ENTRY cue 0 when the credits already pay
    /// for a player, else cue 1 ("insert coins"), which repeats (traced session2-coins, session8).
    /// </summary>
    public void Advance()
    {
        if (_entry is null)
            return;
        _ticks++;
        if (_rejectOnLoad && Parts.PollReady())
        {
            _rejectOnLoad = false;
            RejectCard();
        }
        if (_ticks == _rejectUntil)
        {
            Parts.HideCardBand();
            if (_joined.Count == 0 && Card is null && !canPay())
                ReturnToAttract?.Invoke();
        }
        if (_timerStarted && Parts.Countdown)
            play(_timerCues.Advance(RemainingSeconds));
        if (_dialogAt >= 0)
            play(_dialogCues.Advance((int)Math.Ceiling(Math.Max(0, CardDialogSeconds - (_ticks - _dialogAt) / 60d))));
        if (_cardReadAt >= 0)
        {
            var since = _ticks - _cardReadAt;
            if (since == DialogDonTicks)
            {
                Don?.SetDialogDon(true);
                call(_entry, "FinishCostumeLoad", number(2));
            }
            else if (since == BandOkTicks)
            {
                Parts.ShowCardBand(IndicatorParts.CardBand.ReadOk);
                PlayCue?.Invoke("SE_COM", 16); // traced with the green band
            }
            else if (since == ServerReplyTicks)
            {
                _cardReadAt = -1;
                Parts.HideCardBand();
                call(_entry, "ReplyServer", LumenHostValue.FromBoolean(true));
            }
        }
        if (Coins is null || _joined.Count != 0)
            return;
        var affordable = Coins.Missing(0) == 0;
        if (_ticks == PromptDelayTicks)
            PlayVoice?.Invoke(affordable ? 0 : 1);
        // ponytail: only the unaffordable prompt was seen repeating; the affordable one plays once.
        else if (_ticks > PromptDelayTicks && !affordable && (_ticks - PromptDelayTicks) % PromptRepeatTicks == 0)
            PlayVoice?.Invoke(1);
    }

    public void AttachEntry(LumenPlayer entry)
    {
        _entry = entry;
        CoinsChanged();
    }

    /// <summary>
    /// UpdateCoins(p1, p2): per side, 1 while the next join is not affordable ("insert coins"), -1 once
    /// it is ("hit the drum"). Traced with 2 credits a player: (1, 1) at 1 credit, (-1, -1) at 2, and
    /// (1, 1) again after P1 paid.
    /// </summary>
    public void CoinsChanged()
    {
        // Both drums joined: nothing left to pay for; the game sends no more (traced session9, session13).
        if (_entry is not { } entry || _joined.Count >= 2) return;
        var flag = number(Coins is null || Coins.Missing(_joined.Count) == 0 ? -1 : 1);
        call(entry, "UpdateCoins", flag, flag);
    }

    /// <summary>InitInfo: the game resets both players' entry panels and the coin state (traced).</summary>
    public void InitInfo()
    {
        if (_entry is not { } entry) return;
        var no = LumenHostValue.FromBoolean(false);
        if (Card is not null)
            call(entry, "InputCardReader");
        call(entry, "SetPlayer", number(0), no, no, no);
        call(entry, "SetPlayer", number(1), no, no, no);
        call(entry, "BnCoinInit", number(0), no, no, no);
    }

    /// <summary>The entry's IsReady poll; the game answers 0 until its parts are set up.</summary>
    public bool PollReady() => Parts.PollReady();

    /// <summary>Remaining countdown seconds (whole, rounded up); the full time while not running out.</summary>
    public int RemainingSeconds => (int)Math.Ceiling(Math.Max(0, (_timerLimit - _timer.Elapsed).TotalSeconds));

    /// <summary>The entry movie's Lumen methods beyond the common front-end ones.</summary>
    public void Register(LumenHostObject lumen)
    {
        ArgumentNullException.ThrowIfNull(lumen);
        lumen.RegisterMethod("IsTimeStarted", _ => LumenHostValue.FromBoolean(_timerStarted));
        lumen.RegisterMethod("StartTimer", call =>
        {
            var seconds = call.Arguments.Length > 0 ? call.Arguments[0].AsNumber() : EntrySeconds;
            _timerLimit = TimeSpan.FromSeconds(seconds);
            _timerStarted = true;
            _timerCues.Reset();
            _timer.Reset(); // runs from ResumeTimer, as the counter does
            Parts.StartCountdown(seconds);
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("ResumeTimer", _ =>
        {
            if (Parts.Countdown) _timer.Start();
            Parts.ResumeCountdown();
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("PauseTimer", _ =>
        {
            _timer.Stop();
            Parts.PauseCountdown();
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("GetTimeSec", _ => number(RemainingSeconds));
        lumen.RegisterMethod("IsTimeup", _ => LumenHostValue.FromBoolean(_timerStarted && RemainingSeconds == 0));
        lumen.RegisterMethod("SetIndicator", call =>
        {
            // The entry also asks for "wait" on a player who has not joined (e.g. while P1 dresses up);
            // ponytail: unverified against the game, which is believed to keep that legend hidden.
            if (call.Arguments.Length >= 2)
            {
                var player = (int)call.Arguments[0].AsNumber();
                Parts.SetIndicator(player, _joined.Contains(player) ? (int)call.Arguments[1].AsNumber() : 0);
            }
            return LumenHostValue.Undefined;
        });
        // UpdateFillrect(index, id, iconType): the costume picker fills list slot icon_<index>L (0-4) or the
        // selected item's name plate name_L (10). ponytail: the left (P1) dialog only.
        lumen.RegisterMethod("UpdateFillrect", call =>
        {
            if (call.Arguments.Length < 3 || _entry is not { } entry || CostumeIcon is null)
                return LumenHostValue.Undefined;
            var index = (int)call.Arguments[0].AsNumber();
            var name = index == 10;
            if (CostumeIcon((int)call.Arguments[2].AsNumber(), (int)call.Arguments[1].AsNumber(), name) is { } surface)
                entry.SetNativeFill(name ? "name_L" : $"icon_{index}L", surface);
            return LumenHostValue.Undefined;
        });
        // NotifyDataSelect(1): a read card waits for a drum (its board fades in); 0: the game hides the
        // boards right after (traced).
        lumen.RegisterMethod("NotifyDataSelect", call =>
        {
            if (Card is not null && call.Arguments.Length > 0 && call.Arguments[0] is var waiting
                && (waiting.Kind == LumenHostValueKind.Boolean ? waiting.AsBoolean() : waiting.AsNumber() != 0))
            {
                Parts.FadeInCardName();
                _dialogAt = _ticks;
                _dialogCues.Reset();
            }
            else
            {
                // The dialog closed without a drum taking the card (つかわない or its countdown): it leaves.
                if (Card is not null)
                    closeCardDialog();
                Card = null;
                Parts.HideNameBoards();
            }
            return LumenHostValue.Undefined;
        });
        // Card sequence (traced session13-card): Wait_AccessServer -> SetCardInfo(2 = no drum yet,
        // media 0, not new) with the name centre-top -> ReplyServer(true); EntryData(side) = the drum
        // hit that takes the card, before that player's EntryCoin. ponytail: a side that already
        // joined as a guest takes the card as it is (the movie offers it; the replace is untraced).
        lumen.RegisterMethod("Wait_AccessServer", _ =>
        {
            if (Card is { } card && _entry is { } entry)
            {
                var no = LumenHostValue.FromBoolean(false);
                CardDialog?.Invoke(true);
                Parts.ShowCardBand(IndicatorParts.CardBand.Reading);
                call(entry, "SetCardInfo", number(2), number(0), no, no);
                Parts.ShowCardName(TaikoPlayerName(card));
                Don?.SetLook(2, card.Look); // the dialog's Don, before its costume "loads"
                _cardReadAt = _ticks; // Advance: dialog Don, green band, ReplyServer
            }
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("EntryData", request =>
        {
            if (Card is { } card && request.Arguments.Length > 0 && (int)request.Arguments[0].AsNumber() is var side and (0 or 1))
            {
                Card = null;
                CardClaimed?.Invoke(side, card);
                Parts.ShowEntryName(side, TaikoPlayerName(card), _joined.Contains(side));
                _cardNames[side] = TaikoPlayerName(card);
                closeCardDialog();
                // The drum's Don takes the card player's look: costume slots, then FinishCostumeLoad (traced).
                Don?.SetLook(side, card.Look);
                if (_entry is { } entry)
                {
                    assignCostumes(entry, side, card.Look);
                    call(entry, "FinishCostumeLoad", number(side));
                }
            }
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("GetCoinNum", _ => number(Coins?.Credits ?? 0));
        // Whether the next player can join now (free play, or the credits cover it): the card dialog's
        // pick then joins that side at once through EntryCoin (traced 1 when affordable, 0 when not).
        lumen.RegisterMethod("IsPlayerbleCoinOrFree", _ => LumenHostValue.FromBoolean(canPay()));
        lumen.RegisterMethod("IsPlayerbleCoin", _ => LumenHostValue.FromBoolean(canPay()));
        lumen.RegisterMethod("IsCardReaderError", _ => LumenHostValue.FromBoolean(false));
        // ponytail: modes without a scene yet (AI battle, shop) are reported unavailable. Waiwai is on:
        // the game enables it while online (traced 0 online, 1 offline); this engine has no network gate.
        lumen.RegisterMethod("IsAvailableShop", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("IsAvailableBattle", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("IsWaiwaiDisable", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("NotifyMaccollabo", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("NotifyRecognizecollabo", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("Terminate", _ => LumenHostValue.FromBoolean(true));
        // Traced calls with no visible effect on the entry movies (card reader, indicator lamps, costume
        // effects, mode notifications, counter reports). ponytail: accessories are accepted but not shown yet.
        foreach (var name in new[]
        {
            "SetCardReaderEvent", "Wakeup", "SetTouchMode",
            "ChangeAccessory", "NotifyChangeCostumeEffect",
            "NotifyDecide", "NotifyModeSelectEnd", "Apply", "SelectCommonSound", "NotifyTimeSec",
        })
            lumen.RegisterMethod(name, static _ => LumenHostValue.Undefined);
    }

    /// <summary>
    /// What the game does inside EntryCoin when a player joins, before answering 1: pay the credits
    /// (coin mode), coin state, then the player's costume lists (traced guest defaults). The other
    /// drum may join later (traced session9-2p: EntryCoin(1, 1) answers 0 until paid, then 1).
    /// </summary>
    public bool TryJoin(int player)
    {
        if (player is not (0 or 1) || _joined.Contains(player) || !CanJoin(player) || _entry is not { } entry
            || Coins?.TryJoin(_joined.Count) == false)
            return false;
        var p = number(player);
        _joined.Add(player);
        if (_cardNames.Remove(player, out var cardName))
            Parts.UncoverEntryName(player, cardName);
        // The game's own join voice inside EntryCoin: cue 2 for the left drum, 3 for the right (traced;
        // in free play the movie also requests cue 2).
        PlayVoice?.Invoke(player == 1 ? 3 : 2);
        call(entry, "BnCoinSetFree", LumenHostValue.FromBoolean(false));
        CoinsChanged();
        PlayerJoined?.Invoke(player);
        assignCostumes(entry, player, PlayerLook?.Invoke(player));
        return true;
    }

    private void closeCardDialog()
    {
        _dialogAt = -1;
        Don?.SetDialogDon(false);
        CardDialog?.Invoke(false);
    }

    private readonly Dictionary<int, string> _cardNames = []; // drums holding a card, until they join

    private bool canPay() => Coins is null || Coins.Missing(_joined.Count) == 0;

    private static string TaikoPlayerName(ScoreProfile card) => card.Name.Length > 0 ? card.Name : $"#{card.Baid}";

    /// <summary>
    /// The costume lists and slots of a drum's player; the movie then puts on slot 0. The game does it
    /// when a card is given to a drum (EntryData, before the movie dresses its Don) and at the join
    /// (traced session13-card).
    /// </summary>
    private static void assignCostumes(LumenPlayer entry, int player, DonLook? look)
    {
        var p = number(player);
        // ponytail: traced guest costume set; a profile player gets their current look in slot 0, which
        // the movie then puts on (traced carded join: slot 0 = kigurumi 32, then ChangeCostume(0, 32)).
        // Their owned lists and presets (slots 1-2) are not served yet.
        int[] worn = look?.Costume is [var wornWhole, var wornHead, var wornBody, var wornPaint, ..] ? [wornWhole, wornHead, wornBody, wornPaint] : [0, 0, 0, 0];
        call(entry, "ReleaseCostume", p);
        foreach (var id in new[] { 0, 4, 7, 14, worn[0] }.Distinct()) call(entry, "AssignCostume", p, number(id));
        foreach (var id in new[] { 0, 10, 21, 22, worn[1] }.Distinct()) call(entry, "AssignCosHead", p, number(id));
        foreach (var id in new[] { 0, 2, 20, 21, worn[2] }.Distinct()) call(entry, "AssignCosBody", p, number(id));
        foreach (var (slot, head, body) in new[] { (0, 22, 21), (1, 21, 20), (2, 10, 2) })
        {
            var current = slot == 0 && look is not null;
            call(entry, "AssignSlotCostume", p, number(slot), number(current ? worn[0] : 0));
            call(entry, "AssignSlotHeadBody", p, number(slot), number(current ? worn[1] : head), number(current ? worn[2] : body));
            call(entry, "AssignSlotPaintAcce", p, number(slot), number(current ? worn[3] : 0), number(0));
        }
        call(entry, "AssignRandomCostume", p, number(0));
        call(entry, "AssignRandomHeadBody", p, number(5), number(21));
        call(entry, "AssignRandomPaintAcce", p, number(7), number(0));
        call(entry, "InitCostumeList", p);
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments) =>
        LumenPlayerCalls.Call(player, name, arguments);
}

internal static class LumenPlayerCalls
{
    public static void Call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Entry movie is missing callback '{name}'.");
    }
}
