using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>A side's 良, 可, 不可 and 連打 in one AI battle section.</summary>
public readonly record struct AiSectionCounts(int Great, int Good, int Miss, int Rolls);

/// <summary>One AI battle section: whether the player took it, and both sides' counts.</summary>
public readonly record struct AiBattleSection(bool Won, AiSectionCounts Player, AiSectionCounts Ai);

/// <summary>An AI battle song's sections, for its results.</summary>
public sealed record AiBattleOutcome(IReadOnlyList<AiBattleSection> Sections)
{
    public int SectionsWon => Sections.Count(static section => section.Won);

    /// <summary>The battle is the player's: most sections taken.</summary>
    public bool Won => SectionsWon * 2 > Sections.Count;
}

/// <summary>
/// AI battle's section results (ghost/result's result_section, traced session-ai-battle-3): at load the sections' win or
/// loss, both sides' counts per section (section 1-5, player, AI-don), the rank and reward numbers; the game calls Start
/// once the normal results close (NotifyCloseNormalResult), and the movie calls the host's Start when it is done.
/// Its sounds: PlaySound(421, cue) = VO_AIBTL_SEL.
/// </summary>
/// <remarks>ponytail: rank, tokens and reward numbers are a new player's until AI battle progress is kept.</remarks>
public sealed class AiSectionResultBinding(AiBattleOutcome outcome, Action<string, int>? playCue) : ILumenHostBinding
{
    private LumenPlayer? _movie;
    private bool _started;

    /// <summary>The results backdrop (result_bg): SetWinTotalFlag with the setup.</summary>
    public Func<LumenPlayer?>? Backdrop { get; init; }

    /// <summary>The section results have played out (the movie called Start).</summary>
    public bool Ended { get; private set; }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var name in new[] { "LumenMethod", "Lumen" })
            context.RegisterObject(name, method =>
            {
                method.RegisterMethod("Start", _ =>
                {
                    Ended = true;
                    return LumenHostValue.Undefined;
                });
                method.RegisterMethod("PlaySound", call =>
                {
                    if (call.Arguments.Length >= 2 && call.Arguments[0].Kind == LumenHostValueKind.Number
                        && call.Arguments[0].AsNumber() == 421 && call.Arguments[1].Kind == LumenHostValueKind.Number)
                        playCue?.Invoke("VO_AIBTL_SEL", (int)call.Arguments[1].AsNumber());
                    return LumenHostValue.Undefined;
                });
                method.RegisterMethod("SelectCommonSound", static _ => LumenHostValue.Undefined);
            });
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _movie = player;
        Poll();
    }

    private bool _sent;

    /// <summary>Each tick: the setup goes out once the movie has registered its callbacks (after its first frame).</summary>
    public void Poll()
    {
        if (_sent || _movie is not { CallbackNames.Length: > 0 })
            return;
        _sent = true;
        var no = LumenHostValue.FromBoolean(false);
        call("SetPlayerJoin", LumenHostValue.FromBoolean(true), no);
        for (var section = 0; section < outcome.Sections.Count; section++)
            call("SetWinOrLoseSection", number(section + 1), LumenHostValue.FromBoolean(outcome.Sections[section].Won));
        call("SetWinTokenNumber", number(0), number(0), no);
        call("SetBonusNumberFlag", no, no);
        call("SetRankNumber", number(0), number(0));
        call("SetNextRankNumber", number(1));
        call("SetNextRewardNumber", number(1), no);
        void rows(string name, Func<AiSectionCounts, int> value)
        {
            for (var section = 0; section < outcome.Sections.Count; section++)
                call(name, number(section + 1), number(value(outcome.Sections[section].Player)), number(value(outcome.Sections[section].Ai)));
        }
        rows("SetRyoNoteNumber", static counts => counts.Great);
        rows("SetKaNoteNumber", static counts => counts.Good);
        rows("SetFukaNoteNumber", static counts => counts.Miss);
        rows("SetRendaNoteNumber", static counts => counts.Rolls);
        // SetWinNumber(wins before, wins gained): any gained means the battle was won (the movie's is_win_total).
        call("SetWinNumber", number(0), number(outcome.Won ? 1 : 0));
        Backdrop?.Invoke()?.TryInvokeCallback("SetWinTotalFlag", [LumenHostValue.FromBoolean(outcome.Won)]);
    }

    // Traced: the section results run about 13 s from their Start to the game moving on.
    private static readonly TimeSpan PlayTime = TimeSpan.FromSeconds(13);
    private readonly System.Diagnostics.Stopwatch _since = new();

    /// <summary>The section results have had their time since they started.</summary>
    public bool Played => _started && _since.Elapsed >= PlayTime;

    /// <summary>The normal results closed: the section results play (traced result_section.Start).</summary>
    public void Start()
    {
        Poll();
        if (_started || !_sent) return;
        _started = true;
        _since.Restart();
        call("Start");
    }

    private void call(string name, params LumenHostValue[] arguments) => _movie?.TryInvokeCallback(name, arguments);

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
}
