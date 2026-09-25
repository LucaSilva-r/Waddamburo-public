using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class ScoreSavingTests
{
    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108));

    private static PlayableChart chart(string key = "chart", double secondNote = 1.5) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), key),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(3),
        [
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(secondNote), PlayableNoteKind.BigKa),
            new PlayableHitObject(TimeSpan.FromSeconds(2), PlayableNoteKind.Don),
        ],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)]);

    [Fact]
    public void ChartHashIsPinnedAndFollowsOnlyPlayableContent()
    {
        var hash = ChartHash.Compute(chart(), TaikoCourse.Oni);
        // Pinned: a change here orphans every saved score. Bump ChartHash.FormatVersion deliberately.
        Assert.Equal("46b0c1e01317155879d8f6a45e92da1e01403106a43ceedd98b9f7f5dce3a2e9", hash);
        Assert.Equal(hash, ChartHash.Compute(chart(key: "renamed"), TaikoCourse.Oni));
        Assert.NotEqual(hash, ChartHash.Compute(chart(secondNote: 1.6), TaikoCourse.Oni));
        Assert.NotEqual(hash, ChartHash.Compute(chart(), TaikoCourse.Hard));
    }

    [Fact]
    public void ReplayReproducesTheJudgementAfterEncoding()
    {
        var played = new TaikoJudgementSession(chart(), Windows, TimeSpan.FromMilliseconds(30));
        var replay = new TaikoReplay();
        foreach (var (action, ms) in new[]
        {
            (TaikoInputAction.LeftDon, 1_010), (TaikoInputAction.LeftKa, 1_540), (TaikoInputAction.RightKa, 1_550),
            (TaikoInputAction.LeftKa, 2_000),
        })
        {
            replay.Add(action, TimeSpan.FromMilliseconds(ms));
            played.SubmitInput(action, TimeSpan.FromMilliseconds(ms));
        }
        played.AdvanceTo(TimeSpan.FromSeconds(3));

        var replayed = new TaikoJudgementSession(chart(), Windows, TimeSpan.FromMilliseconds(30));
        TaikoReplay.Decode(replay.Encode()).Play(replayed);

        Assert.Equal(played.CreateSnapshot().ToArray(), replayed.CreateSnapshot().ToArray());
        Assert.Equal(TaikoHitResult.Miss, replayed.CreateSnapshot()[2].Result); // wrong colour
    }

    [Fact]
    public void StoreKeepsEveryPlayAndDerivesTheBestCrown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            var key = chart().Key;
            PlayRecord play(bool cleared, int miss) => new(Guid.NewGuid(), 42, "sha", key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3 - miss, 0, miss, 3 - miss, 0, 50, cleared),
                DateTimeOffset.UtcNow, new TaikoReplay().Encode());
            using (var store = new ScoreStore(path))
            {
                store.Save(play(cleared: true, miss: 1));
                Assert.Equal(TaikoCrown.Clear, store.Crowns(42)[key.ToString()]);
                store.Save(play(cleared: true, miss: 0));
                store.Save(play(cleared: false, miss: 3));
                Assert.Empty(store.Crowns(7));
            }
            using var reopened = new ScoreStore(path); // migrations run once
            Assert.Equal(TaikoCrown.FullCombo, reopened.Crowns(42)[key.ToString()]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
