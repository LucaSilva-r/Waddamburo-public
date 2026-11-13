using System.Net;
using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class ScoreSavingTests
{
    [Fact]
    public void CorruptScoresFailWithoutReplacingThePlayersDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-corrupt-scores-{Guid.NewGuid():N}.db");
        byte[] corrupt = System.Text.Encoding.UTF8.GetBytes("This is a synthetic corrupt database, not SQLite.");
        try
        {
            File.WriteAllBytes(path, corrupt);

            var exception = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            {
                using var store = new ScoreStore(path);
            });

            Assert.Equal(26, exception.SqliteErrorCode); // SQLITE_NOTADB
            Assert.Equal(corrupt, File.ReadAllBytes(path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108));

    private static PlayableChart chart(string key = "chart", double secondNote = 1.5, SongSourceKind source = SongSourceKind.Tja) => new(
        new ChartKey(new SongKey(source, "song"), key),
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
    public void AnOsuStablePlayUploadsAsAnOsuChart()
    {
        // Same chart id and rules as lazer's: the server rescores and ranks it as OsuLazer.
        var upload = ChartUpload.From(chart(source: SongSourceKind.OsuStable), TaikoCourse.Oni, null);
        Assert.Equal(("OsuLazer", "local:song"), (upload.Source, upload.SongKey));
        Assert.Equal(ChartUpload.From(chart(source: SongSourceKind.OsuLazer), TaikoCourse.Oni, null).Notes, upload.Notes);
    }

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

    private static PlayableChart rescoreChart() => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.FromTicks(12345),
        TimeSpan.FromSeconds(6),
        [
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(1.5), PlayableNoteKind.BigKa) { InRun = true },
            new PlayableHitObject(TimeSpan.FromSeconds(2), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(5), PlayableNoteKind.BigDon, isHand: true),
        ],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(2), 150.5, 3, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1), new ChartScrollPoint(TimeSpan.FromSeconds(1), 1.25)],
        [new ChartEffectPoint(TimeSpan.Zero, false), new ChartEffectPoint(TimeSpan.FromSeconds(1.4), true)],
        [new ChartBarLine(TimeSpan.Zero, true), new ChartBarLine(TimeSpan.FromSeconds(2), false)],
        [
            new PlayableLongNote(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3), PlayableLongNoteKind.Roll),
            new PlayableLongNote(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(4.5), PlayableLongNoteKind.Balloon, 3),
        ]) { Level = 8, ScoreInit = 1000, ScoreDiff = 250 };

    [Fact]
    public void AStoredChartReadsBackToTheSameHash()
    {
        var bytes = ChartHash.Serialize(rescoreChart(), TaikoCourse.Ura);
        var (read, course) = ChartHash.Deserialize(bytes, SongSourceKind.OsuLazer);

        Assert.Equal(TaikoCourse.Ura, course);
        Assert.Equal(bytes, ChartHash.Serialize(read, course));
        Assert.Equal(SongSourceKind.OsuLazer, read.Key.Song.Source);
        Assert.True(read.HitObjects[3].IsHand);
        Assert.Throws<InvalidDataException>(() => ChartHash.Deserialize([.. bytes, 0], SongSourceKind.Tja));
    }

    [Fact]
    public void RescoringAReplayGivesThePlaysResult()
    {
        var replay = new TaikoReplay();
        replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        replay.Add(TaikoInputAction.LeftKa, TimeSpan.FromSeconds(1.55)); // Good, then its second hit
        replay.Add(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1.56));
        for (var ms = 2500; ms < 3000; ms += 5) // 200 hits a second: 60 a second count
            replay.Add(ms % 10 == 0 ? TaikoInputAction.LeftDon : TaikoInputAction.RightDon, TimeSpan.FromMilliseconds(ms));
        for (var ms = 3600; ms < 3900; ms += 100)
            replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromMilliseconds(ms));
        // The don at 2 s and the big don at 5 s pass unhit.
        var (chart, course) = ChartHash.Deserialize(ChartHash.Serialize(rescoreChart(), TaikoCourse.Oni), SongSourceKind.Tja);

        var result = PlayRescore.Score(chart, course, TaikoPlayOptions.None, null, TaikoReplay.Decode(replay.Encode()),
            Windows);

        Assert.Equal((1, 1, 2, 2), (result.Great, result.Good, result.Miss, result.MaxCombo));
        // Drumroll hits only (the balloon's 3 are not counted), beside the half-second roll's 30 frames.
        Assert.Equal((30, 30), (result.Rolls, result.RollMax));
        // 1000 + Good in Go-Go (1000 / 20 * 10 * 1.2) doubled (a big note always scores big), 30 rolls x 120, 2 x 360 + 6000.
        Assert.Equal(1000 + 1200 + 30 * 120 + 2 * 360 + 6000, result.Score);
        Assert.Equal(PlayRescore.Score(rescoreChart(), course, TaikoPlayOptions.None, null, replay, Windows), result);
    }

    [Fact]
    public void RankingBoardPlacesAPlayOnlyWhenItBeatsThePlayersLine()
    {
        RankingEntry[] top = [new(1, "a", 900), new(2, "b", 800), new(3, "c", 700)];
        var placed = RankingBoard.Place(top, 3, "c", 850);
        Assert.Equal(1, placed.RankIn);
        Assert.Equal([1L, 3, 2], placed.Top.Select(line => line.Baid));
        Assert.Equal(-1, RankingBoard.Place(top, 2, "b", 750).RankIn);
        Assert.Equal(-1, RankingBoard.Place(top, 9, "z", 700).RankIn); // a tie stays behind
        Assert.Equal(0, RankingBoard.Place([], 9, "z", 10).RankIn);
    }

    [Fact]
    public void ReplayReproducesTheJudgementAfterEncoding()
    {
        var played = new TaikoJudgementSession(chart(), Windows);
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

        var replayed = new TaikoJudgementSession(chart(), Windows);
        TaikoReplay.Decode(replay.Encode()).Play(replayed);

        var actual = replayed.CreateSnapshot();
        var expected = played.CreateSnapshot();
        Assert.Equal(expected.Take(2).ToArray(), actual.Take(2).ToArray());
        Assert.Equal(TaikoHitResult.Miss, actual[2].Result); // wrong colour was ignored; note timed out
    }

    [Fact]
    public void PlaysUnderOlderRulesAreListedWithTheirChartUntilRescored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var osu = chart(source: SongSourceKind.OsuLazer);
            var sha = ChartHash.Compute(osu, TaikoCourse.Oni);
            var replay = new TaikoReplay();
            replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
            var (old, current) = (Guid.NewGuid(), Guid.NewGuid());
            foreach (var (id, version) in new[] { (old, PlayRecord.CurrentScoringVersion - 1), (current, PlayRecord.CurrentScoringVersion) })
                store.Save(new PlayRecord(id, 0, sha, osu.Key, "normal", new TaikoPlayResult(TaikoCourse.Oni, 999, 3, 0, 0, 3, 50, 50, true)
                    { Options = TaikoPlayOptions.FromBits(64) }, DateTimeOffset.UtcNow, replay.Encode()) { ScoringVersion = version, Seed = 7 },
                    ChartUpload.From(osu, TaikoCourse.Oni, null));

            var outdated = Assert.Single(store.OutdatedPlays(PlayRecord.CurrentScoringVersion));
            Assert.Equal((old, "OsuLazer", 64, 7), (outdated.Id, outdated.Source, outdated.Options, outdated.Seed));
            Assert.Equal(replay.Encode(), outdated.Replay);
            Assert.Equal(Convert.FromBase64String(ChartUpload.From(osu, TaikoCourse.Oni, null).Notes), outdated.Notes);

            store.UpdateScore(old, new TaikoPlayResult(TaikoCourse.Oni, 500, 1, 0, 2, 1, 0, 3, false), PlayRecord.CurrentScoringVersion);
            Assert.Empty(store.OutdatedPlays(PlayRecord.CurrentScoringVersion));
            Assert.Equal(500, store.PreviousBest(0, sha, current));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SyncUploadsPendingPlaysAndRequestedChartsOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var osu = chart(source: SongSourceKind.OsuLazer);
            var song = new SongDescriptor(osu.Key.Song, new SongTitle("千本桜", "千本桜", "Senbonzakura"), "Kurousa-P",
                [new SongChartDescriptor(osu.Key, "Inner Oni", new CatalogAssetKey(new CatalogProviderId("osu-lazer"), "chart"),
                    TaikoCourse.Oni) { OnlineId = 123 }]) { OnlineSetId = 45 };
            var sha = ChartHash.Compute(osu, TaikoCourse.Oni);
            store.Save(new PlayRecord(Guid.NewGuid(), 42, sha, osu.Key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                new TaikoReplay().Encode()) { AudioOffsetMs = 12, InputOffsetMs = -8, EngineVersion = "1.0.0+abc" }, ChartUpload.From(osu, TaikoCourse.Oni, song));
            var server = new StubServer(sha);
            using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
            var client = new ScoreClient(http);

            Assert.Equal(1, await client.SyncAsync(store, 42));
            Assert.Equal(0, await client.SyncAsync(store, 42));
            Assert.Contains("\"chart_sha256\":\"" + sha, server.Plays);
            Assert.Contains("\"max_combo\":3", server.Plays);
            Assert.Contains("\"audio_offset_ms\":12", server.Plays);
            Assert.Contains("\"input_offset_ms\":-8", server.Plays);
            // The website's metadata survives the local database.
            var uploaded = JsonDocument.Parse(server.Chart!).RootElement;
            Assert.Equal("Senbonzakura", uploaded.GetProperty("title_en").GetString());
            Assert.Equal("Inner Oni", uploaded.GetProperty("difficulty").GetString());
            Assert.Equal(123, uploaded.GetProperty("osu_beatmap_id").GetInt32());
            Assert.Equal(45, uploaded.GetProperty("osu_beatmapset_id").GetInt32());
            Assert.Equal("set:45", uploaded.GetProperty("song_key").GetString());
            // The imported notes are the hashed canonical bytes.
            using var gzip = new System.IO.Compression.GZipStream(
                new MemoryStream(Convert.FromBase64String(JsonDocument.Parse(server.Chart!).RootElement.GetProperty("notes").GetString()!)),
                System.IO.Compression.CompressionMode.Decompress);
            using var notes = new MemoryStream();
            gzip.CopyTo(notes);
            Assert.Equal(ChartHash.Serialize(chart(), TaikoCourse.Oni), notes.ToArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DevelopmentBuildPlaysOnlyGoToLocalOrStagingServers()
    {
        Assert.True(ScoreClient.AcceptsDevPlays(new Uri("http://localhost:8000/")));
        Assert.True(ScoreClient.AcceptsDevPlays(new Uri("http://127.0.0.1/")));
        Assert.True(ScoreClient.AcceptsDevPlays(new Uri("https://staging.taikonline.com/")));
        Assert.False(ScoreClient.AcceptsDevPlays(new Uri("https://taikonline.com/")));
        Assert.True(ScoreClient.IsDevVersion("0.26.0-nightly+2968f7d4"));
        Assert.False(ScoreClient.IsDevVersion("0.26.0+abc"));
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var played = chart();
            var sha = ChartHash.Compute(played, TaikoCourse.Oni);
            void save(string version) => store.Save(new PlayRecord(Guid.NewGuid(), 42, sha, played.Key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                new TaikoReplay().Encode()) { EngineVersion = version }, ChartUpload.From(played, TaikoCourse.Oni, null));
            save("0.0.0-dev+abc");
            save("1.0.0+abc");
            var production = new StubServer(sha);
            using (var http = new HttpClient(production) { BaseAddress = new Uri("https://taikonline.com/") })
                Assert.Equal(1, await new ScoreClient(http).SyncAsync(store, 42));
            Assert.Contains("\"1.0.0", production.Plays); // (JSON escapes the +)
            Assert.DoesNotContain("-dev", production.Plays);
            // Dropped, not left pending: a later sync (any server) does not send it.
            Assert.Empty(store.PendingPlays(42, 10));

            save("0.0.0-dev+def");
            var local = new StubServer(sha);
            using (var http = new HttpClient(local) { BaseAddress = new Uri("http://localhost:8000/") })
                Assert.Equal(1, await new ScoreClient(http).SyncAsync(store, 42));
            Assert.Contains("0.0.0-dev", local.Plays);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(42L)]
    [InlineData(null)]
    public async Task DisablingCustomUploadsPausesEarlierOfflinePlaysWithoutBlockingOfficialSongs(long? baid)
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            Guid customId;
            byte[] replay;
            using (var store = new ScoreStore(path))
            {
                var recorded = new TaikoReplay();
                recorded.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
                replay = recorded.Encode();
                // More custom plays than one batch, before the official songs. All share canonical notes:
                // eligibility must follow the individual play's source, not shared chart metadata.
                foreach (var source in new[] { SongSourceKind.Tja, SongSourceKind.OsuLazer, SongSourceKind.OsuStable })
                    for (var index = 0; index < 20; index++)
                        save(store, source, replay);
                customId = store.PendingPlays(42, 1)[0].Id;
                save(store, SongSourceKind.Stock, replay);
                save(store, SongSourceKind.Nijiiro, replay);
                // A guest's custom play must never become eligible, even on a cabinet.
                save(store, SongSourceKind.Tja, replay, player: 0);
            }
            // Startup/offline backlog uses the same policy after reopening the database.
            using var reopened = new ScoreStore(path);
            var server = new StubServer("not-requested");
            using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
            var client = new ScoreClient(http);
            var enabled = false;
            Assert.Equal(2, await client.SyncAsync(reopened, baid, uploadCustomSongs: () => enabled));
            Assert.Equal(0, await client.SyncAsync(reopened, baid, uploadCustomSongs: () => enabled));
            Assert.DoesNotContain(customId.ToString(), server.Plays);
            Assert.Null(server.Chart);
            Assert.Equal(60, reopened.PendingPlays(42, 100).Count);
            var local = reopened.LocalPlays(42, "shared-sha");
            Assert.Equal(62, local.Count);
            Assert.All(local, entry => Assert.Equal(replay, entry.Replay.Encode()));
            Assert.Equal(1000, reopened.PreviousBest(42, "shared-sha", Guid.NewGuid()));

            enabled = true;
            Assert.Equal(60, await client.SyncAsync(reopened, baid, uploadCustomSongs: () => enabled));
            Assert.Empty(reopened.PendingPlays(42, 100));
            Assert.Contains(customId.ToString(), server.Plays);
            // Even after upload, every local replay remains directly available offline.
            Assert.Equal(62, reopened.LocalPlays(42, "shared-sha").Count);
            Assert.Equal(63, reopened.OutdatedPlays(int.MaxValue).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }

        static void save(ScoreStore store, SongSourceKind source, byte[] replay, long player = 42)
        {
            var played = chart(source: source);
            store.Save(new PlayRecord(Guid.NewGuid(), player, "shared-sha", played.Key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                replay) { EngineVersion = "1.0.0" }, ChartUpload.From(played, TaikoCourse.Oni, null));
        }
    }

    [Theory]
    [InlineData(42L)]
    [InlineData(null)]
    public async Task FolderPrivacyProtectsSavedAndQueuedPlaysWithoutBlockingOtherCharts(long? baid)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wdb-marker-{Guid.NewGuid():N}.db");
        var protectedIds = new List<Guid>();
        var queuedIds = new List<Guid>();
        var replay = new TaikoReplay();
        replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        try
        {
            using (var store = new ScoreStore(path))
            {
                // Folder privacy is per play, even when private and public charts share the same notes/hash.
                for (var index = 0; index < 55; index++)
                {
                    protectedIds.Add(save(store, "protected", true));
                    queuedIds.Add(save(store, "queued", false));
                }
                save(store, "public", false);
            }
            using var reopened = new ScoreStore(path);
            var server = new StubServer("not-requested");
            using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
            var client = new ScoreClient(http);
            // A marker added after saving also blocks the offline backlog. More than a batch cannot starve public plays.
            Assert.Equal(1, await client.SyncAsync(reopened, baid, canUploadChart: key => !key.EndsWith("/queued", StringComparison.Ordinal)));
            Assert.All(protectedIds.Concat(queuedIds), id => Assert.DoesNotContain(id.ToString(), server.Plays));
            Assert.Equal(111, reopened.LocalPlays(42, "shared").Count);
            Assert.Null(server.Chart);
            // Removing a marker allows the earlier backlog, but never plays saved under folder protection.
            Assert.Equal(55, await client.SyncAsync(reopened, baid, canUploadChart: _ => true));
            Assert.All(protectedIds, id => Assert.DoesNotContain(id.ToString(), server.Plays));
            var local = reopened.LocalPlays(42, "shared");
            Assert.Equal(111, local.Count);
            Assert.All(local, entry => Assert.Equal(replay.Encode(), entry.Replay.Encode()));
            Assert.Equal(1000, reopened.PreviousBest(42, "shared", Guid.NewGuid()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }

        Guid save(ScoreStore store, string name, bool blocked)
        {
            var played = chart(key: name);
            var id = Guid.NewGuid();
            store.Save(new PlayRecord(id, 42, "shared", played.Key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                replay.Encode()) { EngineVersion = "1.0.0", UploadBlocked = blocked }, ChartUpload.From(played, TaikoCourse.Oni, null));
            return id;
        }
    }

    [Theory]
    [InlineData(SongSourceKind.Tja)]
    [InlineData(SongSourceKind.OsuLazer)]
    [InlineData(SongSourceKind.OsuStable)]
    [InlineData(SongSourceKind.Stock)]
    [InlineData(SongSourceKind.Nijiiro)]
    public async Task PlaysMadeWithCustomUploadsOffNeverBecomePendingAfterReenabling(SongSourceKind source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wdb-private-plays-{Guid.NewGuid():N}.db");
        var played = chart(source: source);
        var upload = ChartUpload.From(played, TaikoCourse.Oni, null);
        var official = source is SongSourceKind.Stock or SongSourceKind.Nijiiro;
        var replay = new TaikoReplay();
        replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        var oldIds = new List<Guid>();
        try
        {
            using (var store = new ScoreStore(path))
            {
                foreach (var mode in new[] { "normal", "ai_battle" })
                {
                    var play = record(mode);
                    oldIds.Add(play.Id);
                    store.Save(play, upload, uploadCustomSongs: false);
                }
                Assert.Equal(official ? 2 : 0, store.PendingPlays(42, 50).Count);
                Assert.Equal(official ? 2 : 0, store.PendingPlays(null, 50).Count);
            }
            // Restart with uploads enabled: private history must still be excluded before any network request.
            using var reopened = new ScoreStore(path);
            var server = new StubServer("shared");
            using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
            var client = new ScoreClient(http);
            Assert.Equal(official ? 2 : 0, await client.SyncAsync(reopened, 42, uploadCustomSongs: () => true));
            if (!official)
            {
                Assert.Empty(server.Plays);
                Assert.Null(server.Chart);
            }
            foreach (var mode in new[] { "normal", "ai_battle" })
                reopened.Save(record(mode), upload, uploadCustomSongs: true);
            Assert.Equal(2, await client.SyncAsync(reopened, null, uploadCustomSongs: () => true));
            Assert.Empty(reopened.PendingPlays(null, 50));
            if (!official)
                Assert.All(oldIds, id => Assert.DoesNotContain(id.ToString(), server.Plays));
            Assert.Equal(4, reopened.LocalPlays(42, "shared").Count);
            Assert.All(reopened.LocalPlays(42, "shared"), entry => Assert.Equal(replay.Encode(), entry.Replay.Encode()));
            Assert.Equal(1000, reopened.PreviousBest(42, "shared", Guid.NewGuid()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }

        PlayRecord record(string mode) => new(Guid.NewGuid(), 42, "shared", played.Key, mode,
            new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
            replay.Encode()) { EngineVersion = "1.0.0" };
    }

    private sealed class StubServer(string missingChart) : HttpMessageHandler
    {
        public string Plays { get; private set; } = "";
        public string? Chart { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == $"/api/wdb/charts/{missingChart}")
            {
                Chart = body;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Plays += body;
            var id = JsonDocument.Parse(body).RootElement.GetProperty("plays")[0].GetProperty("id").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"accepted\":[\"{id}\"],\"missing_charts\":[\"{missingChart}\"]}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void PairingShowsTheCodeTakesAClaimedCardOnceAndResetsWhenClosed()
    {
        var pairing = new PairingClient(new HttpClient(), "0123abcd");
        Assert.Equal(new PairingState.Active("661722", TimeSpan.FromSeconds(30)),
            pairing.Apply("status=active\nsession=s1\ncode=661722\nexpires_in=30\n"));
        var claim = "status=claimed\nsession=s1\ncommand_id=c1\naccess_code=30800000000000000001\n";
        Assert.Equal(new PairingState.Claimed("30800000000000000001"), pairing.Apply(claim));
        Assert.IsType<PairingState.Closed>(pairing.Apply(claim)); // re-sent until acknowledged: taken once
        Assert.IsType<PairingState.Closed>(pairing.Apply("status=complete\nsession=s1\n"));
        Assert.Equal(new PairingState.Active("123456", TimeSpan.FromSeconds(30)), // a fresh session after complete
            pairing.Apply("status=active\nsession=s2\ncode=123456\nexpires_in=30\n"));
        Assert.IsType<PairingState.Closed>(pairing.Apply("status=closed\n"));
        Assert.IsType<PairingState.Closed>(pairing.Apply("status=active\ncode=12345\nexpires_in=30\n"));
    }

    [Fact]
    public void ProfileLookBecomesTheDonsCostumeAndColours()
    {
        var profile = JsonSerializer.Deserialize<ScoreProfile>("""
            {"baid":24,"name":"x","look":{"costume":[32,0,0,0,0],"face":"#b3dbff","body":"#dd1400","limb":"#b9b9b9"}}
            """, ScoreClient.Json)!;
        Assert.Equal(Waddamburo.Game.Don.DonCostume.FromWhole(32), profile.Look!.ToCostume());
        Assert.Equal((0xB3DBFFu, 0xDD1400u, 0xB9B9B9u), profile.Look.Colors);
        Assert.Equal(new Waddamburo.Game.Don.DonCostume(null, 5, 6, 7),
            (profile.Look with { Costume = [0, 5, 6, 7, 0] }).ToCostume());
        Assert.Equal(0, profile.Look.DanRank);
        using var pushed = JsonDocument.Parse("""
            {"baid":24,"name":"x","look":{"costume":[36,0,0,0,0],"face":"#b3dbff","body":"#dd1400","limb":"#b9b9b9","dan_rank":12}}
            """);
        Assert.Equal(12, ScoreClient.ParseProfile(pushed.RootElement)!.Look!.DanRank);
    }

    [Fact]
    public void UpdatingDeletesFailedPlays()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            var key = chart().Key;
            PlayRecord play(bool cleared) => new(Guid.NewGuid(), 42, "sha", key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, cleared), DateTimeOffset.UtcNow, new TaikoReplay().Encode());
            var upload = ChartUpload.From(chart(), TaikoCourse.Oni, null);
            using (var store = new ScoreStore(path))
            {
                store.Save(play(cleared: true), upload);
                store.Save(play(cleared: false), upload);
                // A failed play stays on this PC: never pending upload, yet the results' best counts it.
                Assert.True(Assert.Single(store.PendingPlays(42, 50)).Cleared);
                var failed = play(cleared: false) with { Result = new TaikoPlayResult(TaikoCourse.Oni, 2000, 3, 0, 0, 3, 0, 50, false) };
                store.Save(failed, upload);
                Assert.Equal(2000, store.PreviousBest(42, "sha", Guid.NewGuid()));
                var local = store.LocalPlays(42, "sha");
                Assert.Equal(3, local.Count); // failed and pending successful plays, newest first, with their replays
                Assert.Equal(2000, local[0].Row.Score);
                Assert.All(local, static play => Assert.True(play.Row.LocalOnly));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Back to version 8: the later steps run again (the AI battle columns go first, ADD COLUMN can't repeat).
                command.CommandText = """
                    ALTER TABLE plays DROP COLUMN ai_won; ALTER TABLE plays DROP COLUMN ai_sections;
                    ALTER TABLE plays DROP COLUMN ai_support; ALTER TABLE plays DROP COLUMN ai_points;
                    ALTER TABLE plays DROP COLUMN hit_median; ALTER TABLE plays DROP COLUMN hit_variance;
                    ALTER TABLE plays DROP COLUMN ai_session_id; ALTER TABLE plays DROP COLUMN ai_ranked;
                    ALTER TABLE plays DROP COLUMN ai_base_sha256; ALTER TABLE plays DROP COLUMN ai_prior_median;
                    ALTER TABLE plays DROP COLUMN ai_prior_variance;
                    ALTER TABLE plays DROP COLUMN upload_blocked;
                    ALTER TABLE plays DROP COLUMN ai_section_counts;
                    DROP TABLE remote_ai_plays;
                    PRAGMA user_version = 8;
                    """;
                command.ExecuteNonQuery();
            }
            using var updated = new ScoreStore(path);
            Assert.True(Assert.Single(updated.PendingPlays(42, 50)).Cleared);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void AiBattleStandingDerivesFromLocalAndServerBattles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var key = chart().Key;
            var id = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow;
            // A failed battle can still be a win: it uploads (an AI battle) and counts.
            store.Save(new PlayRecord(id, 42, "sha", key, "ai_battle",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 10, false), at, new TaikoReplay().Encode())
            {
                Battle = new AiBattlePlay(true, "WLWWL", 0, 3) { Ranked = true, SessionId = id, BaseSha256 = "base", Prior = (4.5, 300) },
                Timing = (12, 400),
                Seed = 1234,
            }, ChartUpload.From(chart(), TaikoCourse.Oni, null));
            var pending = Assert.Single(store.PendingPlays(42, 50));
            Assert.Equal(("ai_battle", true, "WLWWL", 3, 12d), (pending.Mode, pending.AiWon, pending.AiSections, pending.AiPoints, pending.HitMedianMs));
            Assert.Equal(1234, pending.Seed);
            Assert.Equal(id, pending.AiSessionId);
            Assert.Equal(("base", 4.5, 300d), (pending.AiBaseSha256, pending.AiPriorMedianMs, pending.AiPriorVariance));
            Assert.True(store.HasAiWin(42, "sha"));
            Assert.False(store.HasAiWin(42, "other"));
            // The server's battles add up once each: this PC's play (by id) is not counted twice.
            store.ReplaceRemoteAiPlays(42, [
                new RemoteAiPlay(id, "sha", true, 3, 12, 400, at),
                new RemoteAiPlay(Guid.NewGuid(), "other", true, 2, -5, 900, at.AddDays(-1)),
                new RemoteAiPlay(Guid.NewGuid(), "other", false, 0, null, null, at.AddDays(-2)),
            ]);
            Assert.Equal(new AiProgress(5, 2, (12, 400)), store.AiProgress(42));
            Assert.True(store.HasAiWin(42, "other"));
            Assert.Equal(AiProgress.None with { }, store.AiProgress(7));
            // The server's verified outcome replaces a provisional local claim for the same id.
            store.ReplaceRemoteAiPlays(42, [new RemoteAiPlay(id, "sha", false, 0, 5, 100, at)]);
            Assert.Equal(new AiProgress(0, 0, (5, 100)), store.AiProgress(42));
            Assert.False(store.HasAiWin(42, "sha"));
            store.MarkUploaded([id]);
            store.ReplaceRemoteAiPlays(42, []);
            Assert.Equal(new AiProgress(0, 0, (12, 400)), store.AiProgress(42));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
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
            var upload = ChartUpload.From(chart(), TaikoCourse.Oni, null);
            using (var store = new ScoreStore(path))
            {
                store.Save(play(cleared: true, miss: 1), upload);
                Assert.Equal(TaikoCrown.Clear, store.Crowns(42)[key.ToString()]);
                store.Save(play(cleared: true, miss: 0), upload);
                store.Save(play(cleared: false, miss: 3), upload);
                Assert.Empty(store.Crowns(7));
                // A home guest's play is kept, but never offered for upload.
                store.Save(play(cleared: true, miss: 0) with { Baid = ScoreProfile.LocalGuestBaid }, upload);
                Assert.Equal(TaikoCrown.FullCombo, store.Crowns(ScoreProfile.LocalGuestBaid)[key.ToString()]);
                Assert.DoesNotContain(store.PendingPlays(null, 50), static pending => pending.Baid == ScoreProfile.LocalGuestBaid);
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

    [Fact]
    public void ServerBestsBecomeCrownsThroughTheChartHashAndCountAsPreviousBest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var played = chart().Key;
            var other = chart(key: "other").Key;
            // Played elsewhere only: no crown until this PC knows the chart's hash.
            store.ReplaceRemoteBests(42, [new RemoteBest("remote-sha", 900, 2), new RemoteBest("sha", 500, 1)]);
            Assert.Empty(store.Crowns(42));
            store.SaveChartHashes([(other.ToString(), "remote-sha")]);
            Assert.Equal(TaikoCrown.FullCombo, store.Crowns(42)[other.ToString()]);

            // A local play and a remote best on the same chart: the better crown, the better score.
            var id = Guid.NewGuid();
            Assert.Equal(500, store.PreviousBest(42, "sha", id));
            store.Save(new PlayRecord(id, 42, "sha", played, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 700, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                new TaikoReplay().Encode()), ChartUpload.From(chart(), TaikoCourse.Oni, null));
            Assert.Equal(TaikoCrown.FullCombo, store.Crowns(42)[played.ToString()]);
            Assert.Equal(500, store.PreviousBest(42, "sha", id));
            Assert.Equal(700, store.PreviousBest(42, "sha", Guid.NewGuid()));
            Assert.Null(store.PreviousBest(7, "sha", id));

            // History: this PC's play merged with the server's last plays (charts this PC has hashed), newest first.
            store.ReplaceRemoteBests(42, [], [new RemotePlay("remote-sha", 3, DateTimeOffset.UtcNow.AddMinutes(1)),
                new RemotePlay("unknown-sha", 3, DateTimeOffset.UtcNow.AddMinutes(2))]);
            Assert.Equal([other.ToString(), played.ToString()], store.RecentCharts(42));
            Assert.Empty(store.RecentCharts(7));

            // A fresh download replaces the old one.
            store.ReplaceRemoteBests(42, []);
            Assert.False(store.Crowns(42).ContainsKey(other.ToString()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void ACoursesBestIsItsHighestResultAndStaysOutOfSongScores()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var store = new ScoreStore(path);
            var course = new Waddamburo.Game.Dan.DanCourse(7, "Test", "course", [], []);
            Waddamburo.Game.Dan.DanCourseResult result(int level) => new(course, [new(0, 3, 7, default)], 1, default, level);
            store.SaveDan(new DanPlayRecord(Guid.NewGuid(), 5, result(1), ["a"], DateTimeOffset.UtcNow, [[1]]));
            store.SaveDan(new DanPlayRecord(Guid.NewGuid(), 5, result(0), [null], DateTimeOffset.UtcNow, [[2]]));

            Assert.Equal(1, store.DanBests(5)["course"]);
            Assert.Empty(store.DanBests(6));
            Assert.Empty(store.PendingPlays(5, 10));
            var pending = store.PendingDanPlays(5, 10);
            Assert.Equal(2, pending.Count);
            Assert.Equal(Convert.ToBase64String([1]), pending[0].Songs[0].Replay);
            store.MarkDanUploaded(pending.Select(static play => play.Id));
            Assert.Empty(store.PendingDanPlays(5, 10));
            Assert.Equal(1, store.DanBests(5)["course"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
