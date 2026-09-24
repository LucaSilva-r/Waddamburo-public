using System.Globalization;
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

    /// <summary>The game re-hides the name boards whenever the entry reports its data select state.</summary>
    public void HideNameBoards()
    {
        foreach (var board in _nameBoards)
            call(board, "SetVisible", LumenHostValue.FromBoolean(false));
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
            var name = Gameplay.TaikoGuest.Name(sides[index]);
            var characters = StringInfo.GetTextElementEnumerator(name);
            call(board, "SetPlayer", number(sides[index]));
            call(board, "SetKinotake", number(-1));
            call(board, "SetTitleName", LumenHostValue.FromString(""));
            call(board, "SetTitlePanelID", number(0));
            call(board, "SetDani", number(0), LumenHostValue.FromBoolean(false));
            call(board, "SetCover", LumenHostValue.FromBoolean(false));
            call(board, "SetNameSize", number(new StringInfo(name).LengthInTextElements));
            for (var character = 0; characters.MoveNext(); character++)
                call(board, "SetChar", number(character), LumenHostValue.FromString(characters.GetTextElement()));
            call(board, "Apply");
            call(board, "SetVisible", LumenHostValue.FromBoolean(true));
        }
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
                    call(player, "SetFinalStage", LumenHostValue.FromBoolean(false));
                    call(player, "SetMusicNum", number(1));
                    call(player, "SetVisible", LumenHostValue.FromBoolean(true));
                }
                break;
            case "over_msg":
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
