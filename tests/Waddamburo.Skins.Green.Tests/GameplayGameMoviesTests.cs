using Waddamburo.Game.Tests;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Skins.Green.Gameplay;

namespace Waddamburo.Skins.Green.Tests;

/// <summary>The gameplay adapter: the long notes' published counts and events become the game's overlay calls.</summary>
public sealed class GameplayGameMoviesTests
{
    private readonly List<(string, double)> _lane = [], _counter = [], _balloon = [], _kusudama = [];
    private readonly GameplayGameMovies _movies;

    public GameplayGameMoviesTests()
    {
        var lane = TestMovies.RecordingMovie(_lane).Player;
        _movies = new GameplayGameMovies(lane, null, static () => 0, null, lane, null, null, null, lane, null, null,
            TestMovies.RecordingMovie(_counter).Player, TestMovies.RecordingMovie(_balloon).Player,
            TestMovies.RecordingMovie(_kusudama).Player);
    }

    [Fact]
    public void RollCounterShowsEachCountAndEndsOnlyAfterAHit()
    {
        state("play.roll", LumenHostValue.Undefined); // a roll without a hit
        state("play.roll", number(1));
        state("play.roll", number(2));
        state("play.roll", number(2));
        state("play.roll", LumenHostValue.Undefined);

        Assert.Equal([("SetRendaCount", 1d), ("SetRendaCount", 2d), ("RendaEnd", double.NaN)], _counter);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void BalloonOpensCountsDownAndEnds(bool popped, double result)
    {
        _movies.Event("balloon.started", [number(2), LumenHostValue.FromBoolean(false)]);
        state("play.balloon", number(1));
        _movies.Event("balloon.ended", [LumenHostValue.FromBoolean(popped)]);
        state("play.balloon", LumenHostValue.Undefined);

        Assert.Equal([("Reset", double.NaN), ("SetGekiRendaCount", 0d), ("SetGekiRendaCount", 1d), ("GekiRendaEnd", result)],
            _balloon);
    }

    [Fact]
    public void KusudamaIsToldItsPlayersQuotaAndOnlyChangedCounts()
    {
        _movies.Start(routes: false, kusudamaPlayers: 2, waiwai: true);
        _movies.Event("kusudama.started", [number(3), LumenHostValue.FromBoolean(true)]);
        state("play.kusudama", number(3)); // AddNorma showed it already
        state("play.kusudama", number(2));
        state("play.kusudama", number(0)); // the burst is told by the end
        _movies.Event("kusudama.ended", [LumenHostValue.FromBoolean(true)]);
        state("play.kusudama", LumenHostValue.Undefined);

        Assert.Equal([("SetPlayerNum", 2d), ("SetWaiWai", double.NaN), ("AddNorma", 3d), ("SetGogoTime", double.NaN),
            ("SetCount", 2d), ("EndResult", 0d)], _kusudama);
    }

    private void state(string name, LumenHostValue value) => _movies.State(name, value);

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
}
