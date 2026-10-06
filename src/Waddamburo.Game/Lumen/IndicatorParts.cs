using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum IndicatorPartsScene
{
    Entry,
    SongSelect,
}

/// <summary>
/// The indicator movies a front-end scene loads beside its own (header, name boards, countdown,
/// messages), set up as the game does (traced, research/traces). Parts register their callbacks on
/// their first frame, so setup waits for <see cref="PollReady"/>, driven by the scene's init poll.
/// </summary>
public sealed class IndicatorParts(IndicatorPartsScene scene, bool countdown)
{
    /// <summary>Host id of indicator part layers.</summary>
    public const string HostId = "indicator-part";

    private readonly List<(LumenPlayer Player, Action Setup)> _pending = [];
    private LumenPlayer? _counter;
    private LumenPlayer? _indicator;
    private readonly List<LumenPlayer> _nameBoards = [];

    /// <summary>The cabinet's countdown option: when off, timers show but never run out.</summary>
    public bool Countdown { get; } = countdown;

    /// <summary>Song select's song number (N曲目) and whether it is the credit's last song.</summary>
    public int MusicNumber { get; init; } = 1;
    public bool FinalStage { get; init; }

    public void Attach(string movie, LumenPlayer player) => _pending.Add((player, () => setup(movie, player)));

    /// <summary>Runs pending setups whose callbacks exist; true once all parts are set up.</summary>
    public bool PollReady()
    {
        _pending.RemoveAll(part =>
        {
            if (part.Player.CallbackNames.Length == 0) return false;
            part.Setup();
            return true;
        });
        return _pending.Count == 0;
    }

    /// <summary>Host StartTimer: the counter counts itself (countdown on) or just shows the time.</summary>
    public void StartCountdown(double seconds)
    {
        if (_counter is not { } counter) return;
        if (Countdown)
            call(counter, "Start", number(seconds), LumenHostValue.FromBoolean(true));
        else
        {
            call(counter, "SetTime", number(seconds));
            call(counter, "Stop");
        }
    }

    public void StopCountdown() => callCounter("Stop");

    /// <summary>Lumen.SetIndicator(player, bits): the header's per-player legend (0 = not joined).</summary>
    public void SetIndicator(int player, int bits)
    {
        if (_indicator is { } indicator)
            call(indicator, "SetType", number(player), number(bits));
    }

    /// <summary>
    /// The game re-hides the name boards whenever the entry reports its data select state; a board a
    /// card was given to stays (traced session13-card).
    /// </summary>
    /// <summary>Shows or hides a drum's entry name board (the home setup's "not playing" side).</summary>
    public void SetEntryBoardVisible(int side, bool visible)
    {
        if (_nameBoards.Count >= 3)
            call(_nameBoards[1 + side], "SetVisible", LumenHostValue.FromBoolean(visible));
    }

    public void HideNameBoards()
    {
        for (var index = 0; index < _nameBoards.Count; index++)
            if (!_claimedBoards.Contains(index))
                call(_nameBoards[index], "SetVisible", LumenHostValue.FromBoolean(false));
    }

    private LumenPlayer? _overMessage;

    /// <summary>over_msg messages (its LABEL table): the card band while reading, then its result.</summary>
    public enum CardBand { Reading = 0, ReadOk = 2, ReadNg = 7 }

    /// <summary>
    /// The card band (traced session15-card-band): over_msg SetType(clear_reading) and TweenVisible(true,
    /// 15, 0) when the card is read, SetType(clear_reading_ok) green 0.7 s later (error_reading_ng red on
    /// a failure), faded out at ReplyServer.
    /// </summary>
    public void ShowCardBand(CardBand band)
    {
        if (_overMessage is not { } overMessage)
            return;
        call(overMessage, "SetType", number((int)band));
        if (band != CardBand.ReadOk) // green replaces the reading band in place; the others appear
            call(overMessage, "TweenVisible", LumenHostValue.FromBoolean(true), number(15), number(0));
    }

    public void HideCardBand()
    {
        if (_overMessage is { } overMessage)
            call(overMessage, "TweenVisible", LumenHostValue.FromBoolean(false), number(15), number(0));
    }

    // Entry boards (FlowScenes): 0 the card's centre board, 1 the left drum's, 2 the right drum's.
    private readonly HashSet<int> _claimedBoards = [];

    /// <summary>The drum holding a card joined: its board comes out of grey (traced SetCover(false), no Fadein).</summary>
    public void UncoverEntryName(int side, string name)
    {
        if (_nameBoards.Count >= 3)
            GuestNameBoard.Show(_nameBoards[1 + side], side, name, visible: false);
    }

    /// <summary>A card was read: its name goes on the centre board (SetPlayer 2), shown by <see cref="FadeInCardName"/>.</summary>
    public void ShowCardName(string name, Scores.ScoreProfile? card = null)
    {
        if (_nameBoards.Count > 0)
            GuestNameBoard.Show(_nameBoards[0], 2, name, visible: false, profile: card);
    }

    /// <summary>Home setup: the choice on a drum's stand changed; its board shows that profile's title (no fade).</summary>
    public void RetitleEntryName(int side, Scores.ScoreProfile profile)
    {
        if (_nameBoards.Count >= 3)
            GuestNameBoard.Show(_nameBoards[1 + side], side, "", profile: profile);
    }

    /// <summary>NotifyDataSelect(1): the card waits for a drum; its board fades in.</summary>
    public void FadeInCardName()
    {
        if (_nameBoards.Count > 0)
            call(_nameBoards[0], "Fadein");
    }

    /// <summary>
    /// EntryData(side): the card's name moves to that drum's board, greyed until that player joins
    /// (<see cref="UncoverEntryName"/>).
    /// </summary>
    public void ShowEntryName(int side, string name, bool joined, Scores.ScoreProfile? profile = null)
    {
        if (_nameBoards.Count < 3)
            return;
        var board = _nameBoards[1 + side];
        GuestNameBoard.Show(board, side, name, visible: false, cover: !joined, profile: profile);
        call(board, "Fadein");
        _claimedBoards.Add(1 + side);
        call(_nameBoards[0], "SetVisible", LumenHostValue.FromBoolean(false));
    }

    /// <summary>
    /// Populate and show the guest name boards when Song Select starts: board n shows the player on
    /// sides[n]; boards beyond the players stay hidden (traced two boards for two players).
    /// </summary>
    public void ShowGuestNames(IReadOnlyList<int> sides)
    {
        if (scene != IndicatorPartsScene.SongSelect)
            return;
        for (var index = 0; index < _nameBoards.Count; index++)
        {
            var board = _nameBoards[index];
            if (index >= sides.Count)
            {
                call(board, "SetVisible", LumenHostValue.FromBoolean(false));
                continue;
            }
            GuestNameBoard.Show(board, sides[index]);
        }
    }

    /// <summary>Song Select: a name board fades out under its player's option board, and back (traced Fadeout / Fadein).</summary>
    public void FadeSongSelectName(int board, bool visible)
    {
        if (scene == IndicatorPartsScene.SongSelect && board < _nameBoards.Count)
            call(_nameBoards[board], visible ? "Fadein" : "Fadeout");
    }

    public void PauseCountdown() => callCounter("Pause");

    public void ResumeCountdown() => callCounter("Resume");

    private void setup(string movie, LumenPlayer player)
    {
        var entry = scene == IndicatorPartsScene.Entry;
        switch (movie)
        {
            case "indicator":
                _indicator = player;
                call(player, "SetScene", number(entry ? 1 : 2));
                call(player, "SetType", number(0), number(0));
                call(player, "SetType", number(1), number(0));
                // Song select: the joined player's legend comes on right away (traced; P1 only for now).
                if (!entry)
                    call(player, "SetType", number(0), number(2));
                break;
            case "player_name":
                // The Song Select guest board is populated after all parts have registered callbacks.
                _nameBoards.Add(player);
                call(player, "SetVisible", LumenHostValue.FromBoolean(false));
                break;
            case "time_counter":
                _counter = player;
                call(player, "SetMusicNum", number(0));
                call(player, "SetTime", number(entry ? 50 : 100));
                if (entry)
                    call(player, "SetShadow", LumenHostValue.FromBoolean(true));
                else
                {
                    call(player, "SetFinalStage", LumenHostValue.FromBoolean(FinalStage));
                    call(player, "SetMusicNum", number(MusicNumber));
                    call(player, "SetVisible", LumenHostValue.FromBoolean(true));
                }
                break;
            case "over_msg":
                _overMessage = player;
                call(player, "TweenVisible", LumenHostValue.FromBoolean(false), number(0), number(0));
                break;
        }
    }

    private void callCounter(string name)
    {
        if (_counter is { } counter) call(counter, name);
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Indicator movie is missing callback '{name}'.");
    }
}
