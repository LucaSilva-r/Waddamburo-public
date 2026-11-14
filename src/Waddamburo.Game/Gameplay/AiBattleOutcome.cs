namespace Waddamburo.Game.Gameplay;

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
