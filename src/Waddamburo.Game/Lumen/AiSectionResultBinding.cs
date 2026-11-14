using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>A side's 良, 可, 不可 and 連打 in one AI battle section.</summary>
public readonly record struct AiSectionCounts(int Great, int Good, int Miss, int Rolls)
{
    /// <summary>
    /// Beats <paramref name="other"/> as result_section's IsHiScoreFlag ranks a section: more 良, then more 可, then fewer
    /// 不可, then more 連打.
    /// </summary>
    public bool Beats(AiSectionCounts other) =>
        Great != other.Great ? Great > other.Great
        : Good != other.Good ? Good > other.Good
        : Miss != other.Miss ? Miss < other.Miss
        : Rolls > other.Rolls;

    /// <summary>Sections as kept with a play: "良,可,不可,連打" per section, separated by ';'.</summary>
    public static string Format(IEnumerable<AiSectionCounts> sections) =>
        string.Join(';', sections.Select(static counts => $"{counts.Great},{counts.Good},{counts.Miss},{counts.Rolls}"));

    /// <summary>The sections <see cref="Format"/> wrote; null if the text is not that.</summary>
    public static AiSectionCounts[]? Parse(string text)
    {
        var sections = text.Split(';');
        var counts = new AiSectionCounts[sections.Length];
        for (var section = 0; section < sections.Length; section++)
        {
            var parts = sections[section].Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var great) || !int.TryParse(parts[1], out var good)
                || !int.TryParse(parts[2], out var miss) || !int.TryParse(parts[3], out var rolls))
                return null;
            counts[section] = new(great, good, miss, rolls);
        }
        return counts;
    }
}

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
/// loss, the player's counts per section (section 1-5, the player's best there before this play, this play: all-zero
/// bests read as a first play, and the movie itself marks 自己ベスト更新), the rank and reward numbers; the game calls Start
/// once the normal results close (NotifyCloseNormalResult), and the movie calls the host's Start when it is done.
/// Its sounds: PlaySound(call type, cue), see <see cref="Bank"/>.
/// </summary>
public sealed class AiSectionResultBinding(AiBattleOutcome outcome, AiResultNumbers numbers, Action<string, int>? playCue,
    IReadOnlyList<AiSectionCounts>? best = null) : ILumenHostBinding
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
                    PlaySound(call, playCue);
                    return LumenHostValue.Undefined;
                });
                method.RegisterMethod("SelectCommonSound", static _ => LumenHostValue.Undefined);
            });
    }

    /// <summary>
    /// The AI results movies' sound call types (their own SoundConstants: Call_Type_*), traced in session-ai-battle-3
    /// (421 → SE_AIBTL_RSLT 0/1/4/8/11/12, 420 → VO_AIBTL_RSLT 5).
    /// </summary>
    public static string? Bank(int callType) => callType switch
    {
        0 => "VO_COM",
        1 => "SE_COM",
        420 => "VO_AIBTL_RSLT",
        421 => "SE_AIBTL_RSLT",
        _ => null,
    };

    /// <summary>LumenMethod.PlaySound(call type, cue) as the AI results movies make it.</summary>
    public static void PlaySound(LumenHostCall call, Action<string, int>? playCue)
    {
        if (call.Arguments.Length >= 2 && call.Arguments[0].Kind == LumenHostValueKind.Number
            && call.Arguments[1].Kind == LumenHostValueKind.Number && Bank((int)call.Arguments[0].AsNumber()) is { } bank)
            playCue?.Invoke(bank, (int)call.Arguments[1].AsNumber());
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
                call(name, number(section + 1), number(value(best is not null && section < best.Count ? best[section] : default)),
                    number(value(outcome.Sections[section].Player)));
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
