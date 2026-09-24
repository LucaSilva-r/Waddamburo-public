using Waddamburo.Game.Flow;
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

    private const int PromptDelayTicks = 50;     // traced 0.83 s after entry loads
    private const int PromptRepeatTicks = 300;   // traced 5.0 s between "insert coins" prompts
    private int _ticks;

    /// <summary>
    /// Per-tick coin-mode prompts before anyone joins: VO_ENTRY cue 0 when the credits already pay
    /// for a player, else cue 1 ("insert coins"), which repeats (traced session2-coins, session8).
    /// </summary>
    public void Advance()
    {
        if (Coins is null || _entry is null || _joined.Count != 0)
            return;
        _ticks++;
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
        if (_entry is not { } entry) return;
        var flag = number(Coins is null || Coins.Missing(_joined.Count) == 0 ? -1 : 1);
        call(entry, "UpdateCoins", flag, flag);
    }

    /// <summary>InitInfo: the game resets both players' entry panels and the coin state (traced).</summary>
    public void InitInfo()
    {
        if (_entry is not { } entry) return;
        var no = LumenHostValue.FromBoolean(false);
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
        lumen.RegisterMethod("NotifyDataSelect", _ =>
        {
            Parts.HideNameBoards(); // traced: the game hides the boards right after
            return LumenHostValue.Undefined;
        });
        lumen.RegisterMethod("GetCoinNum", _ => number(Coins?.Credits ?? 0));
        lumen.RegisterMethod("IsCardReaderError", _ => LumenHostValue.FromBoolean(false));
        // ponytail: modes without a scene yet (AI battle, shop, Waiwai) are reported unavailable.
        lumen.RegisterMethod("IsAvailableShop", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("IsAvailableBattle", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("IsWaiwaiDisable", _ => LumenHostValue.FromBoolean(true));
        lumen.RegisterMethod("NotifyMaccollabo", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("NotifyRecognizecollabo", _ => LumenHostValue.FromBoolean(false));
        lumen.RegisterMethod("Terminate", _ => LumenHostValue.FromBoolean(true));
        // Traced calls with no visible effect on the entry movies (card reader, indicator lamps, costume
        // effects, mode notifications, counter reports). ponytail: accessories are accepted but not shown yet.
        foreach (var name in new[]
        {
            "SetCardReaderEvent", "Wakeup", "SetTouchMode",
            "Wait_AccessServer", "EntryData", "ChangeAccessory", "NotifyChangeCostumeEffect",
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
        // The game's own join voice inside EntryCoin: cue 2 for the left drum, 3 for the right (traced;
        // in free play the movie also requests cue 2).
        PlayVoice?.Invoke(player == 1 ? 3 : 2);
        call(entry, "BnCoinSetFree", LumenHostValue.FromBoolean(false));
        CoinsChanged();
        PlayerJoined?.Invoke(player);
        // ponytail: traced guest costume set; real ownership comes with the costume step.
        call(entry, "ReleaseCostume", p);
        foreach (var id in new[] { 0, 4, 7, 14 }) call(entry, "AssignCostume", p, number(id));
        foreach (var id in new[] { 0, 10, 21, 22 }) call(entry, "AssignCosHead", p, number(id));
        foreach (var id in new[] { 0, 2, 20, 21 }) call(entry, "AssignCosBody", p, number(id));
        foreach (var (slot, head, body) in new[] { (0, 22, 21), (1, 21, 20), (2, 10, 2) })
        {
            call(entry, "AssignSlotCostume", p, number(slot), number(0));
            call(entry, "AssignSlotHeadBody", p, number(slot), number(head), number(body));
            call(entry, "AssignSlotPaintAcce", p, number(slot), number(0), number(0));
        }
        call(entry, "AssignRandomCostume", p, number(0));
        call(entry, "AssignRandomHeadBody", p, number(5), number(21));
        call(entry, "AssignRandomPaintAcce", p, number(7), number(0));
        call(entry, "InitCostumeList", p);
        return true;
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
