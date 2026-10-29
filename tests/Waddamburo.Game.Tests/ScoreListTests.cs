using System.Net;
using System.Text;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class ScoreListTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ReadsTheLeaderboardTheOwnPlaysAndAReplay()
    {
        var replay = new TaikoReplay();
        replay.Add(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        replay.Add(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1.5));
        var playId = Guid.NewGuid();
        const string row = """
            "play_id":"{0}","rank":{1},"baid":7,"name":"Ann","score":950000,"great":100,"good":2,"miss":0,
            "max_combo":102,"rolls":5,"gauge":50,"cleared":true,"crown":2,"course":3,"played_at":"2026-10-06T10:00:00+00:00",
            "audio_offset_ms":10,"input_offset_ms":-5
            """;
        var server = new StubServer(path => path switch
        {
            $"/api/wdb/charts/{Sha}/scores" => $$"""{"scores":[{{{string.Format(System.Globalization.CultureInfo.InvariantCulture, row, playId, 1)}}}],"mine":null}""",
            $"/api/wdb/charts/{Sha}/scores/mine" => $$"""{"scores":[{{{string.Format(System.Globalization.CultureInfo.InvariantCulture, row, playId, "null")}}}]}""",
            _ when path == $"/api/wdb/plays/{playId}/replay" => $$"""{{{string.Format(System.Globalization.CultureInfo.InvariantCulture, row, playId, "null")}},"chart_sha256":"{{Sha}}","replay":"{{Convert.ToBase64String(replay.Encode())}}"}""",
            _ => null,
        });
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };
        var client = new ScoreClient(http);

        var (scores, mine) = await client.LeaderboardAsync(Sha);
        var best = Assert.Single(scores);
        Assert.Equal((playId, 1, "Ann", 950000L, 2, -5), (best.PlayId, best.Rank, best.Name, best.Score, best.Crown, best.InputOffsetMs));
        Assert.Null(mine);
        Assert.Null(Assert.Single(await client.MyScoresAsync(Sha)).Rank);
        var download = await client.ReplayAsync(playId);
        Assert.Equal(Sha, download.ChartSha256);
        Assert.Equal(replay.Inputs, download.Replay.Inputs);
        Assert.Equal(10, download.Play.AudioOffsetMs);
    }

    [Fact]
    public async Task ReportsAPlayAndShowsTheServersAnswer()
    {
        var playId = Guid.NewGuid();
        var server = new StubServer(path => path == $"/api/wdb/plays/{playId}/report" ? """{"message":"Thanks, the report was sent."}""" : null);
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://server.test/") };

        Assert.Equal("Thanks, the report was sent.", await new ScoreClient(http).ReportAsync(playId, "macro"));
        Assert.Equal("""{"reason":"macro"}""", server.LastBody);
    }

    private sealed class StubServer(Func<string, string?> respond) : HttpMessageHandler
    {
        /// <summary>The last request's body (null without one).</summary>
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request.RequestUri!.AbsolutePath) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
