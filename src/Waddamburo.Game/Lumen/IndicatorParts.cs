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
                // ponytail: boards stay hidden until the card / name flow exists.
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
