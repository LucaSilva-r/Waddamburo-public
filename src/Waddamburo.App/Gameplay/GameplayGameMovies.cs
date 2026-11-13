using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// The adapter for one drum's gameplay movies when they are the game's own (movie-provider-design.md, "The game's
/// movies: an adapter"). It hears what the lane publishes, as any movie could, and turns it into the callbacks and
/// label jumps those movies are driven by, in the order the game was traced making them. A movie that reads the
/// provider itself is one of ours: it is left alone, and needs none of these callbacks or labels.
/// </summary>
/// <remarks>
/// Moved here so far: the board's score, the gauge, the gauge's fire, the points popup, the combo sign, the Go-Go
/// splash and the end banner. Still called by the lane's own classes (Waddamburo.Game): the combo count, the hit
/// target, the judgement words, rolls, balloons and the kusudama.
/// </remarks>
internal sealed class GameplayGameMovies(
    LumenPlayer board, LumenPlayer? gauge, Func<int> gaugeSegments, LumenPlayer? scoreAdd, LumenPlayer comboBonus,
    LumenPlayer? gaugeFire, LumenPlayer? goGoSplash, LumenPlayer? banner)
{
    private int _segments = -1;
    private bool _full;

    /// <summary>The movie when it is the game's (it does not read the provider); none, or one of ours: null.</summary>
    private static LumenPlayer? game(LumenPlayer? movie) => movie is { UsesProvider: false } ? movie : null;

    /// <summary>A state of the lane changed (or is told for the first time).</summary>
    public void State(string name, LumenHostValue value)
    {
        switch (name)
        {
            case "play.score" when game(board) is { } movie:
                call(movie, "SetScore", value);
                break;
            // The game's gauge counts in segments: it is told when the count moves, not at every change of the fill.
            case "play.gauge" when gaugeSegments() is var segments && segments != _segments:
                _segments = segments;
                if (game(gauge) is { } gaugeMovie)
                    call(gaugeMovie, "SetCurrentGauge", LumenHostValue.FromNumber(segments));
                break;
            // Traced label jumps: the fire on a full gauge, and out when it drops.
            case "play.gaugeFull" when value.AsBoolean() != _full:
                _full = !_full;
                if (game(gaugeFire) is { } fire) label(fire, _full ? "fever_start" : "fever_end");
                break;
            // The splash at each Go-Go start (one player only; two-player gameplay loads none).
            case "play.goGo" when value.AsBoolean() && game(goGoSplash) is { } splash:
                label(splash, "splash");
                break;
        }
    }

    /// <summary>Something happened on the lane.</summary>
    public void Event(string name, ReadOnlySpan<LumenHostValue> arguments)
    {
        switch (name)
        {
            case "score.added" when game(scoreAdd) is { } movie:
                call(movie, "Create");
                call(movie, "SetCount", arguments[0]);
                break;
            // The movie picks its label from the count; one without the callback shows nothing.
            case "combo.milestone" when game(comboBonus) is { } movie:
                movie.TryInvokeCallback("SetComboBonus", [arguments[0]]);
                break;
            case "song.ended" when game(banner) is { } movie:
                label(movie, TaikoResultBanner.Label(arguments[0].AsBoolean(), arguments[1].AsBoolean()));
                break;
        }
    }

    private static void call(LumenPlayer movie, string name, params LumenHostValue[] arguments)
    {
        if (!movie.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Gameplay movie is missing its {name} callback.");
    }

    private static void label(LumenPlayer movie, string name)
    {
        if (!movie.TryGotoLabel("", name))
            throw new InvalidDataException($"Gameplay movie has no '{name}' label.");
    }
}
