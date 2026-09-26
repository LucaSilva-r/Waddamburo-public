using System.Net;
using System.Text.Json;
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
    public async Task SyncUploadsPendingPlaysAndRequestedChartsOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waddamburo-scores-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ScoreStore(path);
            var sha = ChartHash.Compute(chart(), TaikoCourse.Oni);
            store.Save(new PlayRecord(Guid.NewGuid(), 42, sha, chart().Key, "normal",
                new TaikoPlayResult(TaikoCourse.Oni, 1000, 3, 0, 0, 3, 0, 50, true), DateTimeOffset.UtcNow,
                new TaikoReplay().Encode()), ChartUpload.From(chart(), TaikoCourse.Oni, "Song", null));
            var server = new StubServer(sha);
            using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
            var client = new ScoreClient(http);

            Assert.Equal(1, await client.SyncAsync(store, 42));
            Assert.Equal(0, await client.SyncAsync(store, 42));
            Assert.Contains("\"chart_sha256\":\"" + sha, server.Plays);
            Assert.Contains("\"max_combo\":3", server.Plays);
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
            var upload = ChartUpload.From(chart(), TaikoCourse.Oni, "Song", null);
            using (var store = new ScoreStore(path))
            {
                store.Save(play(cleared: true, miss: 1), upload);
                Assert.Equal(TaikoCrown.Clear, store.Crowns(42)[key.ToString()]);
                store.Save(play(cleared: true, miss: 0), upload);
                store.Save(play(cleared: false, miss: 3), upload);
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
