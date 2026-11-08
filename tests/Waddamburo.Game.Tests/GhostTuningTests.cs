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
          <emotionofs_center>0</emotionofs_center><emotionofs_threslow>5</emotionofs_threslow><emotionofs_threshigh>6</emotionofs_threshigh>
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
        // Level: the rubber band, from the first pattern's spread (+5%) toward the second's (-50%) all the way at 7 points
        // behind, toward the third's (+40%) at 5 ahead; an aggressive AI-don plays as if 6 points further behind.
        Assert.Equal(5, model.VariancePercent(0));
        Assert.Equal(-50, model.VariancePercent(-7));
        Assert.Equal(-50, model.VariancePercent(-9));
        Assert.Equal(40, model.VariancePercent(5));
        Assert.Equal(5 + (40 - 5) * 0.4, model.VariancePercent(2), 6);
        model.Emotion = 4;
        Assert.Equal(-50, model.VariancePercent(-1));
        model.Emotion = 0;
        Assert.Equal(40, model.VariancePercent(0));
        model.Emotion = 2;

        // AI-don replays the player's own offsets (-20 or +60 ms), their distance from the median scaled by the band:
        // far behind (-50%: x0.71) it lands closer, -8 or +48 ms.
        var random = new Random(1);
        Assert.Equal([-8d, 48d], Enumerable.Range(0, 200).Select(_ => Math.Round(model.NextOffset(-7, random).TotalMilliseconds)).Distinct().Order());
    }
}
