using System.Globalization;
using System.Xml.Linq;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// One step of AI battle's rank ladder (the game's <c>config/*/ghost/ghostinfo.xml</c> <c>rankparam</c>): the points that
/// take a player up to it from the rank below, and while at that rank below, the stars a chart needs per course (easy, normal,
/// hard, oni, ura; 0: the course earns nothing) and the stars that earn the high-level bonus (0: none).
/// </summary>
public sealed record AiRankStep(int Points, IReadOnlyList<int> LeastStars, IReadOnlyList<int> BonusStars);

/// <summary>One battle's rank points, and which bonuses they include (the section results show them).</summary>
public readonly record struct AiAward(int Points, bool FirstWin, bool HighLevel);

/// <summary>
/// AI battle's rank points (taiko-fumen wiki, Green's AI battle page): a battle won by most sections at いつもの, its quota
/// cleared, on a chart with the rank's stars for its course, earns 1 point, +1 for the first win on the chart, +1 at the
/// rank's bonus stars. Losing takes nothing away. Points add up to ranks 1..10 (10+ only through the 課題曲 folder: its
/// step asks ★99). Wins are every battle won (any level, cleared or not); rewards come at win counts.
/// </summary>
public sealed class AiBattleRanks(IReadOnlyList<AiRankStep> steps)
{
    /// <summary>Green's ladder as ghostinfo.xml has it (rankparam 1..11; rankparam 0 is rank 0 itself).</summary>
    public static AiBattleRanks Green { get; } = new([
        new(1, [1, 1, 1, 1, 1], [2, 2, 1, 1, 1]),
        new(8, [3, 2, 1, 1, 1], [4, 3, 1, 1, 1]),
        new(15, [5, 4, 1, 1, 1], [0, 5, 2, 1, 1]),
        new(30, [0, 6, 3, 2, 2], [0, 7, 4, 3, 3]),
        new(45, [0, 7, 4, 3, 3], [0, 0, 5, 4, 4]),
        new(60, [0, 0, 6, 5, 5], [0, 0, 7, 6, 6]),
        new(75, [0, 0, 8, 7, 7], [0, 0, 0, 8, 8]),
        new(90, [0, 0, 0, 8, 8], [0, 0, 0, 9, 9]),
        new(105, [0, 0, 0, 9, 9], [0, 0, 0, 10, 10]),
        new(120, [0, 0, 0, 10, 10], [0, 0, 0, 0, 0]),
        new(150, [0, 0, 0, 99, 99], [0, 0, 0, 0, 0]),
    ]);

    /// <summary>The win counts that give a reward (wiki: titles, songs, costumes), in order.</summary>
    public static IReadOnlyList<int> RewardWins { get; } =
    [
        1, 3, 5, 10, 20, 25, 30, 40, 50, 55, 60, 70, 80, 85, 90, 100, 125, 150, 175, 200, 225, 250, 275, 300, 350, 400, 450,
        500, 550, 600, 650, 700, 750, 765, 800, 850, 876, 900, 950, 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1765, 1800,
        1876, 1900, 2000, 2222, 3000, 3333, 4000, 4444, 5000, 5555, 6000, 6666, 7000, 7650, 7777, 8000, 8760, 8888, 9000, 9999,
    ];

    public IReadOnlyList<AiRankStep> Steps { get; } = steps;

    /// <summary>The rank <paramref name="points"/> reach (0..Steps.Count).</summary>
    public int RankOf(int points)
    {
        var rank = 0;
        for (var total = 0; rank < Steps.Count && (total += Steps[rank].Points) <= points; rank++) { }
        return rank;
    }

    /// <summary>Points still needed for the next rank (0 at the top).</summary>
    public int PointsToNextRank(int points)
    {
        var total = 0;
        foreach (var step in Steps)
            if ((total += step.Points) > points)
                return total - points;
        return 0;
    }

    /// <summary>Wins still needed for the next reward (0 once every reward is had).</summary>
    public static int WinsToNextReward(int wins) => NextRewardWins(wins) is var next and > 0 ? next - wins : 0;

    /// <summary>The win count of the next reward (0 once every reward is had).</summary>
    public static int NextRewardWins(int wins) => RewardWins.FirstOrDefault(threshold => threshold > wins);

    /// <summary>The points the step up from <paramref name="rank"/> takes (0 at the top).</summary>
    public int StepSize(int rank) => rank >= 0 && rank < Steps.Count ? Steps[rank].Points : 0;

    /// <summary>The points earned within the current rank (toward the next).</summary>
    public int PointsIntoRank(int points) => points - Steps.Take(RankOf(points)).Sum(static step => step.Points);

    /// <summary>The points one battle earns.</summary>
    /// <param name="points">The player's points before it (their rank sets the conditions).</param>
    /// <param name="course">The chart's course.</param>
    /// <param name="stars">The chart's level (★).</param>
    /// <param name="won">Most sections taken.</param>
    /// <param name="cleared">The quota (ノルマ) met.</param>
    /// <param name="usual">Played at いつもの (no intermediate level).</param>
    /// <param name="firstWin">No earlier win on this chart.</param>
    public AiAward Award(int points, TaikoCourse course, int stars, bool won, bool cleared, bool usual, bool firstWin)
    {
        var rank = RankOf(points);
        if (!won || !cleared || !usual || rank >= Steps.Count)
            return default;
        var step = Steps[rank];
        var least = step.LeastStars.ElementAtOrDefault((int)course);
        if (least <= 0 || stars < least)
            return default;
        var bonus = step.BonusStars.ElementAtOrDefault((int)course);
        var high = bonus > 0 && stars >= bonus;
        return new(1 + (firstWin ? 1 : 0) + (high ? 1 : 0), firstWin, high);
    }

    /// <summary>The ladder in an installation's ghostinfo.xml, or Green's when there is none.</summary>
    public static AiBattleRanks Load(string dataRoot)
    {
        var config = Path.Combine(dataRoot, "config");
        var file = Directory.Exists(config)
            ? Directory.EnumerateFiles(config, "ghostinfo.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        return file is null ? Green : Parse(File.ReadAllText(file));
    }

    public static AiBattleRanks Parse(string xml)
    {
        int value(XElement parent, string name) => int.Parse((string?)parent.Element(name)
            ?? throw new InvalidDataException($"ghostinfo.xml: a rankparam has no {name}."), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var ranks = XDocument.Parse(xml).Descendants("rankparam").ToList();
        return new([.. ranks.Skip(1).Select(rank => new AiRankStep(value(rank, "acquired"),
            [.. "enhmx".Select(course => value(rank, $"rankstar_least_{course}"))],
            [.. "enhmx".Select(course => value(rank, $"rankstar_bonus_{course}"))]))]);
    }
}
