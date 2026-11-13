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
/// Here: the board's score, combo and drum lights, the gauge and its fire, the points popup, the combo sign, the hit
/// target's Go-Go and route, the route bubble, the Go-Go splash and the end banner. Not here: the roll counter, the
/// balloon and the kusudama (TaikoLongNotePresentation calls them, and skips one of ours), and the label jumps
/// that are the lane's drawing itself (hit flashes, judgement words, note faces).
/// </remarks>
internal sealed class GameplayGameMovies(
    LumenPlayer board, LumenPlayer? gauge, Func<int> gaugeSegments, LumenPlayer? scoreAdd, LumenPlayer comboBonus,
    LumenPlayer? gaugeFire, LumenPlayer? goGoSplash, LumenPlayer? banner, LumenPlayer target, LumenPlayer? bubble)
{
    private static readonly string[] Routes = ["normal", "expert", "master"];
    private int _segments = -1;
    private bool _full;
    private int _combo;
    private bool _comboTold; // since the last judged note
    private int _route = -1;

    /// <summary>The board when it is the game's: what the lane sets up on it by hand (side, course, option icons).</summary>
    public LumenPlayer? Board => game(board);

    /// <summary>Traced: SetBranchAvailable(false) for every chart without 譜面分岐; true shows the normal route's lane.</summary>
    public void Start(bool routes)
    {
        if (game(target) is { } movie) call(movie, "SetBranchAvailable", LumenHostValue.FromBoolean(routes));
    }

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
            case "play.combo":
                _combo = (int)value.AsNumber();
                _comboTold = true;
                if (game(board) is { } comboBoard) call(comboBoard, "SetComboCount", value);
                break;
            // The hit target's lane at both edges; the splash at each start (one player only: two load none).
            case "play.goGo":
                if (game(target) is { } lane) call(lane, "SetGogo", value);
                if (value.AsBoolean() && game(goGoSplash) is { } splash) label(splash, "splash");
                break;
            // The first value is where the play starts (normal); from then on the lane takes the route a check
            // picked at once, and the bubble says which way it went (traced: same tick).
            case "play.branch" when Array.IndexOf(Routes, value.AsString()) is var route && route != _route:
                if (_route >= 0)
                {
                    if (game(bubble) is { } bubbleMovie) bubbleMovie.GotoLabel(route > _route ? "level_up" : "level_down", play: true);
                    if (game(target) is { } laneMovie) call(laneMovie, "SetBranch", LumenHostValue.FromNumber(route));
                }
                _route = route;
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
        }
    }

    /// <summary>Something happened on the lane.</summary>
    public void Event(string name, ReadOnlySpan<LumenHostValue> arguments)
    {
        switch (name)
        {
            // The board is told the combo at every judged note, changed or not (a miss on a combo of 0).
            case "note.judged":
                if (!_comboTold && game(board) is { } comboBoard)
                    call(comboBoard, "SetComboCount", LumenHostValue.FromNumber(_combo));
                _comboTold = false;
                break;
            // side: left, right; part: don, ka. The game's board names its lights left_don, right_katsu, ...
            case "drum.hit" when game(board) is { } hitBoard:
                call(hitBoard, "SetTaikoHit", LumenHostValue.FromString(
                    arguments[0].AsString() + (arguments[1].AsString() == "don" ? "_don" : "_katsu")));
                break;
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
