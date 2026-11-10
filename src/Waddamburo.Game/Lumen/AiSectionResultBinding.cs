using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>A side's 良, 可, 不可 and 連打 in one AI battle section.</summary>
public readonly record struct AiSectionCounts(int Great, int Good, int Miss, int Rolls);

/// <summary>One AI battle section: whether the player took it, and both sides' counts.</summary>
public readonly record struct AiBattleSection(bool Won, AiSectionCounts Player, AiSectionCounts Ai);

/// <summary>
/// The section results' standing (traced session-ai-battle-3): rank before and after, the points earned within the rank
/// before the battle and by it (with its first-win and high-level bonuses), the rank's step, the wins before it and the
/// next reward's win count (0: every reward had).
/// </summary>
public sealed record AiResultNumbers(int RankBefore, int RankAfter, int PointsIntoRank, int Gained, bool FirstWinBonus,
    bool HighLevelBonus, int RankStep, int WinsBefore, int NextRewardWins)
{
    public static AiResultNumbers NewPlayer { get; } = new(0, 0, 0, 0, false, false, 1, 0, 1);

    /// <summary>False: an unranked battle (no server setup); it shows no win gained.</summary>
    public bool ShowProgress { get; init; } = true;
}

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
public sealed class AiSectionResultBinding(AiBattleOutcome outcome, AiResultNumbers numbers, Action<string, int>? playCue) : ILumenHostBinding
{
    private LumenPlayer? _movie;
    private bool _started;
    private bool _startRequested;

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
        if (_sent)
        {
            begin();
            return;
        }
        if (_movie is null || !_movie.CallbackNames.Contains("SetWinNumber"))
            return;
        _sent = true;
        var no = LumenHostValue.FromBoolean(false);
        call("SetPlayerJoin", LumenHostValue.FromBoolean(true), no);
        for (var section = 0; section < outcome.Sections.Count; section++)
            call("SetWinOrLoseSection", number(section + 1), LumenHostValue.FromBoolean(outcome.Sections[section].Won));
        // Traced (6, 3, true) for a 3-point win at 6 points into rank 2; (rank before, after); the rank's step (15 at rank 2);
        // the next reward's win count (10 with 9 wins), true once there is none.
        call("SetWinTokenNumber", number(numbers.PointsIntoRank), number(numbers.Gained), LumenHostValue.FromBoolean(numbers.Gained > 0));
        call("SetBonusNumberFlag", LumenHostValue.FromBoolean(numbers.FirstWinBonus), LumenHostValue.FromBoolean(numbers.HighLevelBonus));
        call("SetRankNumber", number(numbers.RankBefore), number(numbers.RankAfter), LumenHostValue.FromBoolean(numbers.RankAfter >= 10));
        call("SetNextRankNumber", number(numbers.RankStep));
        call("SetNextRewardNumber", number(numbers.NextRewardWins), LumenHostValue.FromBoolean(numbers.NextRewardWins == 0));
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
        call("SetWinNumber", number(numbers.WinsBefore), number(numbers.ShowProgress && outcome.Won ? 1 : 0));
        Backdrop?.Invoke()?.TryInvokeCallback("SetWinTotalFlag", [LumenHostValue.FromBoolean(outcome.Won)]);
        begin();
    }

    // Traced: the section results run about 13 s from their Start to the game moving on.
    private static readonly TimeSpan PlayTime = TimeSpan.FromSeconds(13);
    private readonly System.Diagnostics.Stopwatch _since = new();

    /// <summary>The section results have had their time since they started.</summary>
    public bool Played => _started && _since.Elapsed >= PlayTime;

    /// <summary>The normal results closed: the section results play (traced result_section.Start).</summary>
    public void Start()
    {
        _startRequested = true;
        Poll();
    }

    private void begin()
    {
        if (!_startRequested || _started || !_sent) return;
        _started = true;
        _since.Restart();
        call("Start");
    }

    private void call(string name, params LumenHostValue[] arguments) => _movie?.TryInvokeCallback(name, arguments);

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
}
