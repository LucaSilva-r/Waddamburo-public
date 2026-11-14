using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>
/// The adapter for one drum's gameplay movies when they are the game's own (movie-provider-design.md, "The game's
/// movies: an adapter"). It hears what the lane publishes, as any movie could, and turns it into the callbacks and
/// label jumps those movies are driven by, in the order the game was traced making them. A movie that reads the
/// provider itself is one of ours: it is left alone, and needs none of these callbacks or labels.
/// </summary>
/// <remarks>
/// Here: the board's score, combo and drum lights, the gauge and its fire, the points popup, the combo sign, the hit
/// target's Go-Go and route, the route bubble, the Go-Go splash, the end banner, a dan course's panel, the roll counter,
/// the balloon and the kusudama. Not here: the label jumps that are the lane's drawing itself (hit flashes, judgement
/// words, note faces, roll bodies).
/// </remarks>
public sealed class GameplayGameMovies(
    LumenPlayer board, LumenPlayer? gauge, Func<int> gaugeSegments, LumenPlayer? scoreAdd, LumenPlayer comboBonus,
    LumenPlayer? gaugeFire, LumenPlayer? goGoSplash, LumenPlayer? banner, LumenPlayer target, LumenPlayer? bubble,
    LumenPlayer? danPanel, LumenPlayer rollCounter, LumenPlayer balloon, LumenPlayer? kusudama)
{
    private static readonly string[] Routes = ["normal", "expert", "master"];
    private int _segments = -1;
    private bool _full;
    private int _combo;
    private bool _comboTold; // since the last judged note
    private int _route = -1;
    private readonly Dictionary<string, LumenHostValue> _dan = []; // dan.* states, list places as "name#index"
    private bool _danReady, _danTypes, _danValues;
    private int _roll; // the roll count told, 0: none shown
    private int _kusudamaCount; // the kusudama's count as the movie has it

    /// <summary>The board when it is the game's: what the lane sets up on it by hand (side, course, option icons).</summary>
    public LumenPlayer? Board => game(board);

    /// <summary>
    /// Traced: SetBranchAvailable(false) for every chart without 譜面分岐; true shows the normal route's lane. The
    /// kusudama is told the players on it (once per player: two drums share one) and whether it is Waiwai's.
    /// </summary>
    public void Start(bool routes, int kusudamaPlayers, bool waiwai)
    {
        if (game(kusudama) is { } ball)
        {
            call(ball, "SetPlayerNum", number(kusudamaPlayers));
            call(ball, "SetWaiWai", LumenHostValue.FromBoolean(waiwai));
        }
        if (game(target) is { } movie) call(movie, "SetBranchAvailable", LumenHostValue.FromBoolean(routes));
    }

    /// <summary>The movie when it is the game's (it does not read the provider); none, or one of ours: null.</summary>
    private static LumenPlayer? game(LumenPlayer? movie) => movie is { UsesProvider: false } ? movie : null;

    /// <summary>A state of the lane changed (or is told for the first time).</summary>
    public void State(string name, LumenHostValue value)
    {
        if (name.StartsWith("dan.", StringComparison.Ordinal))
            danChanged(name, value);
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
            // The counter appears with the first hit (SetRendaCount(1)) and goes with RendaEnd; a roll without a hit
            // never showed it, so it is not ended either.
            case "play.roll" when game(rollCounter) is { } counter:
                if (value.Kind == LumenHostValueKind.Undefined)
                {
                    if (_roll > 0) call(counter, "RendaEnd");
                    _roll = 0;
                }
                else if ((int)value.AsNumber() is var hits and > 0 && hits != _roll)
                    call(counter, "SetRendaCount", number(_roll = hits));
                break;
            // Hits left; the burst itself is told by balloon.ended, never as 0.
            case "play.balloon" when value.Kind == LumenHostValueKind.Number && value.AsNumber() > 0 && game(balloon) is { } fusen:
                call(fusen, "SetGekiRendaCount", value);
                break;
            // Hits left (with two drums, the one ball's). AddNorma told the movie its count: only a change is sent.
            case "play.kusudama" when game(kusudama) is { } ball:
                if (value.Kind == LumenHostValueKind.Undefined)
                    _kusudamaCount = 0;
                else if ((int)value.AsNumber() is var left and > 0 && left != _kusudamaCount)
                    call(ball, "SetCount", number(_kusudamaCount = left));
                break;
            // Traced label jumps: the fire on a full gauge, and out when it drops.
            case "play.gaugeFull" when value.AsBoolean() != _full:
                _full = !_full;
                if (game(gaugeFire) is { } fire) label(fire, _full ? "fever_start" : "fever_end");
                break;
        }
    }

    /// <summary>A place of a list changed (or is told for the first time).</summary>
    public void Item(string name, int index, LumenHostValue value)
    {
        if (name.StartsWith("dan.", StringComparison.Ordinal))
            danChanged($"{name}#{index}", value);
    }

    private void danChanged(string key, LumenHostValue value)
    {
        _dan[key] = value;
        _danTypes |= key is "dan.song" || key.StartsWith("dan.exam.type#", StringComparison.Ordinal)
            || key.StartsWith("dan.exam.target#", StringComparison.Ordinal);
        _danValues |= key is "dan.notesLeft" || key.StartsWith("dan.exam.value#", StringComparison.Ordinal)
            || key.StartsWith("dan.exam.possible#", StringComparison.Ordinal);
    }

    /// <summary>
    /// The dan panel (traced session30): SetChallengeLevel(rank, 101 for a challenge), SetConditionNum(n),
    /// SetConditionType(i, type, red) at each song, then as the play goes SetLeftNotes(n), SetConditionValue(i, value)
    /// and SetConditionValid(i, 0|1), the whole panel at each change. Its callbacks register on its first frame, so
    /// nothing is told until they are there. Called once a tick, after the lane has published.
    /// </summary>
    public void DanTick()
    {
        if (game(danPanel) is not { } movie || !_dan.TryGetValue("dan.rank", out var rank))
            return;
        if (!_danReady)
        {
            if (!movie.TryInvokeCallback("SetChallengeLevel",
                    [number(rank.AsNumber() == DanRanks.Challenge ? LevelChallenge : rank.AsNumber())]))
                return;
            _danReady = _danTypes = _danValues = true;
            call(movie, "SetConditionNum", _dan["dan.exam.count"]);
        }
        var count = (int)_dan["dan.exam.count"].AsNumber();
        if (_danTypes)
            for (var index = 0; index < count; index++)
                call(movie, "SetConditionType", number(index),
                    number(DanExams.TypeOf(_dan[$"dan.exam.type#{index}"].AsString())), _dan[$"dan.exam.target#{index}"]);
        if (_danValues && _dan.TryGetValue("dan.notesLeft", out var left))
        {
            call(movie, "SetLeftNotes", left);
            for (var index = 0; index < count; index++)
            {
                call(movie, "SetConditionValue", number(index), _dan[$"dan.exam.value#{index}"]);
                call(movie, "SetConditionValid", number(index), number(_dan[$"dan.exam.possible#{index}"].AsBoolean() ? 1 : 0));
            }
        }
        _danTypes = _danValues = false;
    }

    // dani_enso shows its challenge plate (challenge_lv_mini) for LEVEL_CHALLENGE (101) and up; the ranks index its
    // label table (0 初級 ... 24 達人).
    private const int LevelChallenge = 101;

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

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
            // The opening timeline makes the balloon's child two frames after 'start': it is played on before the
            // count is set (play.balloon, right after), which also updates that child.
            case "balloon.started" when game(balloon) is { } fusen:
                call(fusen, "Reset");
                call(fusen, "SetGekiRendaCount", number(0));
                fusen.Advance();
                fusen.Advance();
                break;
            case "balloon.ended" when game(balloon) is { } fusen:
                call(fusen, "GekiRendaEnd", number(arguments[0].AsBoolean() ? 0 : 1));
                break;
            // Reference trace: the quota (added to the one ball by each drum), then whether it is a Go-Go one.
            case "kusudama.started" when game(kusudama) is { } ball:
                call(ball, "AddNorma", arguments[0]);
                call(ball, "SetGogoTime", arguments[1]);
                _kusudamaCount = (int)arguments[0].AsNumber();
                break;
            case "kusudama.ended" when game(kusudama) is { } ball:
                call(ball, "EndResult", number(arguments[0].AsBoolean() ? 0 : 2)); // high / miss
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
