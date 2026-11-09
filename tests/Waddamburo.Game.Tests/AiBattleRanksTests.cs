using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class AiBattleRanksTests
{
    private static readonly int[] Thresholds = [0, 1, 8, 9, 23, 24, 54, 549, 698, 699];

    [Fact]
    public void RanksFollowTheWikisThresholds()
    {
        var ranks = AiBattleRanks.Green;
        // Cumulative: 1, 9, 24, 54, 99, 159, 234, 324, 429, 549, 699 (10+).
        Assert.Equal([0, 1, 1, 2, 2, 3, 4, 10, 10, 11], Thresholds.Select(ranks.RankOf));
        Assert.Equal(1, ranks.PointsToNextRank(0));
        Assert.Equal(8, ranks.PointsToNextRank(1));
        Assert.Equal(3, ranks.PointsToNextRank(6));
        Assert.Equal(0, ranks.PointsToNextRank(699));
        // Traced session-ai-battle-3: rank 2 with 6 of its 15 points; 6 more after a 3-point win.
        Assert.Equal((2, 6, 15), (ranks.RankOf(15), ranks.PointsIntoRank(15), ranks.StepSize(2)));
        Assert.Equal(6, ranks.PointsToNextRank(18));
    }

    [Fact]
    public void PointsNeedAWinAtUsualClearedOnTheRanksStars()
    {
        var ranks = AiBattleRanks.Green;
        // Rank 0: ★1 anywhere; easy/normal bonus at ★2, hard/oni at ★1 (every chart).
        Assert.Equal(3, ranks.Award(0, TaikoCourse.Oni, 8, won: true, cleared: true, usual: true, firstWin: true).Points);
        Assert.Equal(2, ranks.Award(0, TaikoCourse.Oni, 8, won: true, cleared: true, usual: true, firstWin: false).Points);
        Assert.Equal(1, ranks.Award(0, TaikoCourse.Easy, 1, won: true, cleared: true, usual: true, firstWin: false).Points);
        Assert.Equal(0, ranks.Award(0, TaikoCourse.Oni, 8, won: false, cleared: true, usual: true, firstWin: true).Points);
        Assert.Equal(0, ranks.Award(0, TaikoCourse.Oni, 8, won: true, cleared: false, usual: true, firstWin: true).Points);
        Assert.Equal(0, ranks.Award(0, TaikoCourse.Oni, 8, won: true, cleared: true, usual: false, firstWin: true).Points);
        // Rank 3 (24 points): easy earns nothing, normal needs ★6 (bonus ★7).
        Assert.Equal(0, ranks.Award(24, TaikoCourse.Easy, 5, won: true, cleared: true, usual: true, firstWin: true).Points);
        Assert.Equal(0, ranks.Award(24, TaikoCourse.Normal, 5, won: true, cleared: true, usual: true, firstWin: false).Points);
        Assert.Equal(1, ranks.Award(24, TaikoCourse.Normal, 6, won: true, cleared: true, usual: true, firstWin: false).Points);
        Assert.Equal(2, ranks.Award(24, TaikoCourse.Normal, 7, won: true, cleared: true, usual: true, firstWin: false).Points);
        // Rank 10: the 10+ step asks ★99 (the 課題曲 folder only).
        Assert.Equal(0, ranks.Award(549, TaikoCourse.Oni, 10, won: true, cleared: true, usual: true, firstWin: true).Points);
    }

    [Fact]
    public void RewardsComeAtWinCounts()
    {
        Assert.Equal(1, AiBattleRanks.WinsToNextReward(0));
        Assert.Equal(2, AiBattleRanks.WinsToNextReward(1));
        Assert.Equal(5, AiBattleRanks.WinsToNextReward(20));
        Assert.Equal(0, AiBattleRanks.WinsToNextReward(9999));
        Assert.Equal(10, AiBattleRanks.NextRewardWins(9));
    }

    [Fact]
    public void ParsesGhostInfo()
    {
        const string xml = """
            <boost_serialization><ghostinfo><rankparamsize>2</rankparamsize>
            <rankparam><acquired>0</acquired><rewardtitle>0</rewardtitle><rankstar_least_e>0</rankstar_least_e><rankstar_least_n>0</rankstar_least_n><rankstar_least_h>0</rankstar_least_h><rankstar_least_m>0</rankstar_least_m><rankstar_least_x>0</rankstar_least_x><rankstar_bonus_e>0</rankstar_bonus_e><rankstar_bonus_n>0</rankstar_bonus_n><rankstar_bonus_h>0</rankstar_bonus_h><rankstar_bonus_m>0</rankstar_bonus_m><rankstar_bonus_x>0</rankstar_bonus_x></rankparam>
            <rankparam><acquired>1</acquired><rewardtitle>649</rewardtitle><rankstar_least_e>1</rankstar_least_e><rankstar_least_n>1</rankstar_least_n><rankstar_least_h>1</rankstar_least_h><rankstar_least_m>1</rankstar_least_m><rankstar_least_x>1</rankstar_least_x><rankstar_bonus_e>2</rankstar_bonus_e><rankstar_bonus_n>2</rankstar_bonus_n><rankstar_bonus_h>1</rankstar_bonus_h><rankstar_bonus_m>1</rankstar_bonus_m><rankstar_bonus_x>1</rankstar_bonus_x></rankparam>
            </ghostinfo></boost_serialization>
            """;
        var step = Assert.Single(AiBattleRanks.Parse(xml).Steps);
        Assert.Equal(AiBattleRanks.Green.Steps[0].Points, step.Points);
        Assert.Equal(AiBattleRanks.Green.Steps[0].LeastStars, step.LeastStars);
        Assert.Equal(AiBattleRanks.Green.Steps[0].BonusStars, step.BonusStars);
    }
}
