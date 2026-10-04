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
    // The movie does not send an effect notification for every picker preview. Defer the fallback
    // one tick so its own notification and RequestSE can take precedence when it does send them.
    private readonly HashSet<int> _costumeEffectPending = [];
    private readonly HashSet<int> _costumeCuePending = [];
    private readonly HashSet<int> _costumeCueSeen = [];
    private readonly int[] _costumeEffectUntil = [-1, -1];

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

    /// <summary>The name of an account chosen for a drum before the entry (home setup); null: a guest or a card.</summary>
    public Func<int, string?>? PlayerName { get; init; }

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

    /// <summary>Ticks since the entry movie attached (its intro plays over the first ones).</summary>
    public int Ticks => _ticks;

    /// <summary>
    /// Per-tick coin-mode prompts before anyone joins: VO_ENTRY cue 0 when the credits already pay
    /// for a player, else cue 1 ("insert coins"), which repeats (traced session2-coins, session8).
    /// </summary>
    /// <summary>
    /// Home player setup: the entry is the setup screen. Both stands show (DON_BOTH), but nobody really
    /// joins (no voice, coins or PlayerJoined), the mode panels are hidden, and the host decides per
    /// side whether its Don and name board show.
    /// </summary>
    public bool SetupMode { get; set; }

    private readonly bool[] _donHidden = new bool[2], _boardShown = new bool[2];
    private readonly ScoreProfile?[] _setupProfiles = new ScoreProfile?[2];

    /// <summary>Setup: the profile on a side's stand; its title and plate go on that side's name board.</summary>
    public void SetSetupProfile(int side, ScoreProfile? profile)
    {
        if (side is not (0 or 1))
            return;
        _setupProfiles[side] = profile;
        if (_boardShown[side])
            Parts.RetitleEntryName(side, profile ?? ScoreProfile.LocalGuest);
    }

    /// <summary>
    /// Setup: whether a side's Don and name board show (an option replaces the Don with text; a drum
    /// not playing has neither). A board that appears fades in the entry's way.
    /// </summary>
    public void SetSetupSide(int side, bool don, bool board)
    {
        if (_entry is not { } entry || side is not (0 or 1) || !_joined.Contains(side))
            return;
        // The Don clip's alpha is only forced while hidden; showing it again hands it back to the movie.
        if (_donHidden[side] && don)
            entry.TrySetInstanceAlpha(donPath(side), 1);
        _donHidden[side] = !don;
        if (board && !_boardShown[side])
        {
            Parts.ShowEntryName(side, "", joined: true, _setupProfiles[side] ?? ScoreProfile.LocalGuest);
            entry.TrySetInstanceAlpha($"name_bg/touch_msg_{side + 1}p", 0);
        }
        else if (!board && _boardShown[side])
            Parts.SetEntryBoardVisible(side, false);
        _boardShown[side] = board;
    }

    private const int ModeSelectFadeTicks = 15;
    private int _modeSelectFade = -1;

    /// <summary>
    /// Home: the setup's players join the running entry (no reload, so its intro and music carry on).
    /// The movie joined both stands for the setup; a drum not playing keeps its Don and board hidden.
    /// ponytail: the movie still counts that drum as joined; untested whether any mode waits for it.
    /// </summary>
    public void FinishSetup(IReadOnlyList<bool> playing)
    {
        if (!SetupMode || _entry is not { } entry)
            return;
        SetupMode = false;
        _modeSelectFade = 0;
        call(entry, "BnCoinSetFree", LumenHostValue.FromBoolean(false));
        for (var side = 0; side < 2; side++)
        {
            if (!playing[side])
            {
                SetSetupSide(side, don: false, board: false);
                _joined.Remove(side);
                Parts.SetIndicator(side, 0);
                continue;
            }
            PlayerJoined?.Invoke(side);
            assignCostumes(entry, side, PlayerLook?.Invoke(side));
        }
        CoinsChanged();
    }

    private static string donPath(int side) => side == 0 ? "taiko_1p/body/don1pM" : "taiko_2p/body/don2pM";

    public void Advance()
    {
        if (_entry is null)
            return;
        _ticks++;
        if (SetupMode)
            _entry.TrySetInstanceAlpha("modeSelect", 0);
        else if (_modeSelectFade >= 0)
        {
            _modeSelectFade++;
            _entry.TrySetInstanceAlpha("modeSelect", Math.Min(1, _modeSelectFade / (float)ModeSelectFadeTicks));
            if (_modeSelectFade >= ModeSelectFadeTicks)
                _modeSelectFade = -1; // handed back to the movie
        }
        for (var side = 0; side < 2; side++)
            if (_donHidden[side])
                _entry.TrySetInstanceAlpha(donPath(side), 0);
        foreach (var player in _costumeEffectPending)
            startCostumeEffect(player);
        foreach (var player in _costumeCuePending)
            if (!_costumeCueSeen.Contains(player))
                PlayCue?.Invoke("SE_COM", player == 0 ? 19 : 20);
        _costumeEffectPending.Clear();
        _costumeCuePending.Clear();
        _costumeCueSeen.Clear();
        for (var player = 0; player < 2; player++)
            if (_costumeEffectUntil[player] == _ticks)
                stopCostumeEffect(player);
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

    /// <summary>The voice the entry plays when a drum joins (cue 2 left, 3 right; traced in EntryCoin).</summary>
    public void PlayJoinVoice(int player) => PlayVoice?.Invoke(player == 1 ? 3 : 2);

    /// <summary>A changed Don starts the entry's authored costume flash on its next tick.</summary>
    public void CostumeChanged(int player)
    {
        if (player is 0 or 1)
        {
            _costumeEffectPending.Add(player);
            _costumeCuePending.Add(player);
        }
    }

    /// <summary>The movie's own costume cue takes precedence over the fallback cue.</summary>
    public void ObserveCostumeCue(IReadOnlyList<LumenHostValue> arguments)
    {
        if (arguments.Count >= 2 && arguments[0].Kind == LumenHostValueKind.Number
            && arguments[1].Kind == LumenHostValueKind.Number && arguments[0].AsNumber() == 1)
        {
            var cue = (int)arguments[1].AsNumber();
            if (cue is 19 or 20)
                _costumeCueSeen.Add(cue - 19);
        }
    }

    private static string costumeEffectPath(int player) => player == 0
        ? "taiko_1p/body/effect" : "taiko_2p/body/effect";

    private void startCostumeEffect(int player)
    {
        if (_entry is not { } entry)
            return;
        var path = costumeEffectPath(player);
        if (entry.TryGotoLabel(path, "effect_start"))
            entry.TrySetInstanceAlpha(path, 1);
        _costumeEffectUntil[player] = _ticks + 68; // traced effect stop after about 1.13 s
    }

    private void stopCostumeEffect(int player)
    {
        _costumeEffectUntil[player] = -1;
        _entry?.TrySetInstanceAlpha(costumeEffectPath(player), 0);
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
        // Each dialog has five icon slots and a selected-item name plate. The right dialog's
        // indices follow the left one's (5-9 and 11 respectively).
        lumen.RegisterMethod("UpdateFillrect", call =>
        {
            if (call.Arguments.Length < 3 || _entry is not { } entry || CostumeIcon is null)
                return LumenHostValue.Undefined;
            var index = (int)call.Arguments[0].AsNumber();
            if (index is < 0 or > 11)
                return LumenHostValue.Undefined;
            var name = index is 10 or 11;
            var side = index is >= 5 and <= 9 or 11 ? 'R' : 'L';
            var slot = side == 'R' ? index - 5 : index;
            if (CostumeIcon((int)call.Arguments[2].AsNumber(), (int)call.Arguments[1].AsNumber(), name) is { } surface)
                entry.SetNativeFill(name ? $"name_{side}" : $"icon_{slot}{side}", surface);
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
                Parts.ShowCardName(TaikoPlayerName(card), card);
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
        lumen.RegisterMethod("NotifyChangeCostume", static _ => LumenHostValue.Undefined);
        lumen.RegisterMethod("NotifyChangeCostumeEffect", call =>
        {
            if (call.Arguments.Length >= 2 && call.Arguments[0].Kind == LumenHostValueKind.Number
                && (int)call.Arguments[0].AsNumber() is var player and (0 or 1))
            {
                _costumeEffectPending.Remove(player);
                var active = call.Arguments[1].Kind == LumenHostValueKind.Boolean
                    ? call.Arguments[1].AsBoolean() : call.Arguments[1].AsNumber() != 0;
                if (active)
                    startCostumeEffect(player);
                else if (_costumeEffectUntil[player] <= _ticks)
                    stopCostumeEffect(player);
            }
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("Terminate", _ => LumenHostValue.FromBoolean(true));
        // Traced calls with no visible effect on the entry movies (card reader, indicator lamps,
        // mode notifications, counter reports). ChangeAccessory (the puchi) is DonLumenBinding's.
        foreach (var name in new[]
        {
            "SetCardReaderEvent", "Wakeup", "SetTouchMode",
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
        // Home (setup or its players): a name board whose name the host draws over it (account names
        // need Latin letters the board's kana glyphs lack), as for a card taken by that drum.
        else if (!SetupMode && PlayerName?.Invoke(player) is { Length: > 0 })
        {
            Parts.ShowEntryName(player, "", joined: true);
            // The movie hides a drum's card prompt once it has card data (touch_msg_Np._visible =
            // !playerInfo[n].isData); an account never goes through the card path, so hide it here.
            entry.TrySetInstanceAlpha($"name_bg/touch_msg_{player + 1}p", 0); // ponytail: alpha, not isData
        }
        if (SetupMode)
            return true; // both stands up for the setup screen; the real join comes after it
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
        // ponytail: traced guest costume set and lists. A profile player's slots are their costume sets
        // (TaikOnline presets, the worn one first; ponytail: confirm by trace whether the game's slot 0 is
        // the current outfit), else their current look in slot 0, which the movie then puts on (traced:
        // slot 0 head 5 body 6 acce 5, then ChangeDivideCostume(0, 5, 6, 0) and ChangeAccessory(0, 5, 0)).
        // Owned lists are not served yet: the guest lists plus what the slots wear. The sets keep no face
        // paint (the worn one is used); acce is the puchi chara.
        (int Whole, int Head, int Body, int Puchi) wornLook = look?.Costume is [var wornWhole, var wornHead, var wornBody, ..]
            ? (wornWhole, wornHead, wornBody, look.Puchi) : (0, 0, 0, 0);
        var paint = look?.Costume is [_, _, _, var wornPaint, ..] ? wornPaint : 0;
        (int Whole, int Head, int Body, int Puchi)[] slots = look?.Presets is { Length: 3 } presets && presets.All(static set => set.Length >= 4)
            ? [.. presets.Select(static set => (set[0], set[1], set[2], set[3]))
                .OrderBy(set => set != wornLook)] // the worn set first: the movie puts on slot 0
            : look is not null ? [wornLook, (0, 21, 20, 0), (0, 10, 2, 0)]
            : [(0, 22, 21, 0), (0, 21, 20, 0), (0, 10, 2, 0)];
        call(entry, "ReleaseCostume", p);
        foreach (var id in new[] { 0, 4, 7, 14, wornLook.Whole }.Concat(slots.Select(static s => s.Whole)).Distinct()) call(entry, "AssignCostume", p, number(id));
        foreach (var id in new[] { 0, 10, 21, 22, wornLook.Head }.Concat(slots.Select(static s => s.Head)).Distinct()) call(entry, "AssignCosHead", p, number(id));
        foreach (var id in new[] { 0, 2, 20, 21, wornLook.Body }.Concat(slots.Select(static s => s.Body)).Distinct()) call(entry, "AssignCosBody", p, number(id));
        for (var slot = 0; slot < slots.Length; slot++)
        {
            call(entry, "AssignSlotCostume", p, number(slot), number(slots[slot].Whole));
            call(entry, "AssignSlotHeadBody", p, number(slot), number(slots[slot].Head), number(slots[slot].Body));
            call(entry, "AssignSlotPaintAcce", p, number(slot), number(paint), number(slots[slot].Puchi));
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
