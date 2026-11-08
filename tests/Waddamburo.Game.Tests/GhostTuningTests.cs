using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class GhostTuningTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
        <boost_serialization signature="serialization::archive" version="10">
        <tuning_lv0 class_id="0" tracking_level="0" version="2">
          <defvariance_first>1024</defvariance_first><defvariance_second>256</defvariance_second>
          <pattern><tracetype>2</tracetype><traceprob>0</traceprob><variancef>5</variancef></pattern>
          <pattern><tracetype>2</tracetype><traceprob>0</traceprob><variancef>-50</variancef></pattern>
          <pattern><tracetype>2</tracetype><traceprob>0</traceprob><variancef>40</variancef></pattern>
          <patternnode><inferior>-7</inferior><superior>5</superior></patternnode>
          <patternnode><inferior>-1</inferior><superior>0</superior></patternnode>
          <patternnode><inferior>0</inferior><superior>-1</superior></patternnode>
          <statistics_type>0</statistics_type><statistics_samplesize>10</statistics_samplesize>
          <hitscore_ryo>5</hitscore_ryo><hitscore_ka>3</hitscore_ka><hitscore_fuka>0</hitscore_fuka>
        </tuning_lv0>
        </boost_serialization>
        """;

    [Fact]
    public void ParsesLevelsAndLearnsThePlayersTiming()
    {
        var level = Assert.Single(GhostTuning.Parse(Xml));
        Assert.Equal((1024, 256, 10, 5, 3), (level.VarianceFirst, level.VarianceSecond, level.SampleSize, level.ScoreGreat, level.ScoreGood));
        Assert.Equal([5, -50, 40], level.Patterns.Select(static pattern => pattern.VariancePercent));
        Assert.Equal((-7, 5), level.PatternNodes[0]);

        var model = new GhostTimingModel(level);
        Assert.Equal((0d, 1024d), model.Statistics); // no hits yet: the level's own spread
        // A player 20 ms late with a 40 ms spread; only the last 10 hits count.
        foreach (var ms in new[] { 500, 500, -20, 60, -20, 60, -20, 60, -20, 60, -20, 60 })
            model.Observe(TimeSpan.FromMilliseconds(ms));
        Assert.Equal((20d, 1600d), model.Statistics);
        // Behind tightens, ahead loosens.
        Assert.Equal(-50, model.PatternFor(-2).VariancePercent);
        Assert.Equal(40, model.PatternFor(2).VariancePercent);
        Assert.Equal(5, model.PatternFor(0).VariancePercent);

        // AI-don's hits spread as the player's (40 ms), scaled by the pattern: sqrt(1600 * 1.05) = 41 ms around +20.
        var random = new Random(1);
        var offsets = Enumerable.Range(0, 4000).Select(_ => model.NextOffset(0, random).TotalMilliseconds - 20).ToArray();
        Assert.InRange(Math.Sqrt(offsets.Average(static o => o * o)), 38, 44);
    }
}
