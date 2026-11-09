using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// Drives AI battle's own movies through a song (traced session-ai-battle-3): the section timer (SetProgress, percent of
/// the section), the score board (each side's 良 / 可 / 不可 / 連打 this section; side 1 is AI-don's box in its plate), the backdrops' territory needle
/// (UpdateTargetGaugeNum: the player's territory less AI-don's, ±9), the 1/3 and 2/3 cues, and at each section's end the
/// win icon, the board and timer moving on, and the backdrops' win or lose; after the last, the battle's result.
/// Lane 0 is the player's, lane 1 AI-don's.
/// </summary>
internal sealed class AiBattlePresentation
{
    private const int MaxNeedle = 9;
    private static readonly TimeSpan ResultDelay = TimeSpan.FromSeconds(3); // traced: last section end -> StartAction

    private readonly LumenPlayer? _backdrop, _hitEffect, _timer, _board, _icons, _result;
    private readonly TimeSpan[] _ends;
    private readonly TaikoCourse _course;
    private readonly Action<string, int>? _cue;
    private readonly (int Great, int Good, int Miss, int Rolls)[] _base = new (int, int, int, int)[2], _shown = new (int, int, int, int)[2];
    private readonly List<bool> _won = [];
    private readonly List<AiBattleSection> _sections = [];
    private bool _ready, _started, _middle, _closing, _resultShown;
    private int _section, _progress = -1;
    private TimeSpan _endedAt;

    public AiBattlePresentation(LumenGameSceneInstance scene, PlayableChart chart, TaikoCourse course, Action<string, int>? cue)
    {
        LumenPlayer? movie(string name) => scene.Layers.ToList().FindIndex(layer =>
            Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == name) is var index and >= 0 ? scene.Player.Layers[index].Player : null;
        (_backdrop, _hitEffect, _timer, _board, _icons, _result) =
            (movie("ai_enso_bg"), movie("ai_hit_effect"), movie("time"), movie("score_board"), movie("section_win_icon"), movie("battle_result"));
        _ends = AiBattleRules.SectionEnds(chart);
        _course = course;
        _cue = cue;
        for (var lane = 0; lane < 2; lane++)
            _shown[lane] = (-1, -1, -1, -1);
    }

    /// <summary>Each finished section: the player took it.</summary>
    public IReadOnlyList<bool> SectionsWon => _won;

    /// <summary>The finished sections, for the results.</summary>
    public AiBattleOutcome Outcome => new([.. _sections]);

    public int Sections => _ends.Length;

    /// <summary>When battle_result's StartAction went out (chart time); null before.</summary>
    public TimeSpan? ResultShownAt { get; private set; }

    /// <summary>Traced (sessions 3 and 4, 11.3 s both): StartAction to the results; no shutter closes in AI battle.</summary>
    public static readonly TimeSpan ResultsDelay = TimeSpan.FromSeconds(11.3);

    /// <summary>This section's territory so far: the player's and AI-don's.</summary>
    public (int Player, int Ai) Territory => (territory(0), territory(1));

    /// <summary>AI-don's mood (ChangeAIEmotion's 0 more passive .. 2 neutral .. 4 more aggressive).</summary>
    public int Emotion { get; private set; } = 2;

    public void Advance(TimeSpan time, IReadOnlyList<TaikoPlayResult> lanes)
    {
        // The movies register their callbacks on their first frame.
        if (!_ready)
        {
            if (new[] { _backdrop, _hitEffect, _timer, _icons }.Any(static player => player is { CallbackNames.Length: 0 }))
                return;
            _ready = true;
            // Traced AIEnsoBGInitialize(5) / AIEnsoWinIconInitialize(5) with five sections, SetSectionType(1).
            both("AIEnsoBGInitialize", number(_ends.Length));
            call(_timer, "SetSectionType", number(_ends.Length == 5 ? 1 : 0));
            call(_icons, "AIEnsoWinIconInitialize", number(_ends.Length));
        }
        if (!_started)
        {
            _started = true;
            both("StartReadyAction");
            _cue?.Invoke("VO_AIBTL_ENSO", 0);
        }
        traceDons();
        if (_section >= _ends.Length)
        {
            if (!_resultShown && time - _endedAt >= ResultDelay)
            {
                _resultShown = true;
                ResultShownAt = time;
                // ponytail: StartAction(won, false) read from two lost battles (traced (false, false)).
                call(_result, "StartAction", LumenHostValue.FromBoolean(_won.Count(static won => won) * 2 > _won.Count), LumenHostValue.FromBoolean(false));
            }
            return;
        }
        var start = _section == 0 ? TimeSpan.Zero : _ends[_section - 1];
        var end = _ends[_section];
        var fraction = Math.Clamp((time - start) / (end - start), 0, 1);
        if ((int)(fraction * 100) is var progress && progress != _progress)
            call(_timer, "SetProgress", number(_progress = progress));
        if (!_middle && fraction >= 1 / 3d)
            (_middle, _) = (true, call(_backdrop, "OnSectionMiddle"));
        if (!_closing && fraction >= 2 / 3d)
            (_closing, _) = (true, call(_backdrop, "OnSectionClosing"));
        // Each side's 良 and 可 this section.
        for (var lane = 0; lane < Math.Min(2, lanes.Count); lane++)
        {
            var now = countsOf(lanes, lane);
            var counts = (now.Great - _base[lane].Great, now.Good - _base[lane].Good, now.Miss - _base[lane].Miss, now.Rolls - _base[lane].Rolls);
            if (counts == _shown[lane])
                continue;
            _shown[lane] = counts;
            call(_board, "SetPlayerSide", number(lane));
            call(_board, "UpdateValue", number(counts.Item1), number(counts.Item2), number(counts.Item3), number(counts.Item4));
            // Traced: sent with every counted hit, the same value too (the movie's Dons move on each call, and a call
            // ends a section win's attack).
            both("UpdateTargetGaugeNum", number(Math.Clamp(territory(0) - territory(1), -MaxNeedle, MaxNeedle)));
        }
        if (time < end)
            return;
        var won = territory(0) >= territory(1);
        _won.Add(won);
        AiSectionCounts section(int lane) => _shown[lane] is var (great, good, miss, rolls) && great >= 0
            ? new(great, good, miss, rolls) : default;
        _sections.Add(new(won, section(0), section(1)));
        call(_icons, "AIEnsoWinIconMove", LumenHostValue.FromBoolean(won));
        var last = _section == _ends.Length - 1;
        if (!last)
        {
            call(_board, "Reset");
            call(_timer, "ChangeNextSection");
        }
        both(won ? "StartSectionWin" : "StartSectionLose");
        // AI-don's mood (traced, sessions ai-battle-3/4/5: ChangeAIEmotion with the section's start, once the player's
        // wins minus losses reach +2: 4, more aggressive (the angry icon); -2: 0, more passive).
        var net = _won.Count(static section => section) * 2 - _won.Count;
        var emotion = net >= 2 ? 4 : net <= -2 ? 0 : Emotion;
        if (emotion != Emotion)
        {
            Emotion = emotion;
            call(_hitEffect, "ChangeAIEmotion", number(emotion));
        }
        // Traced: VO_AIBTL_ENSO 3 with a section won, 8 with one lost.
        _cue?.Invoke("VO_AIBTL_ENSO", won ? 3 : 8);
        for (var lane = 0; lane < Math.Min(2, lanes.Count); lane++)
            (_base[lane], _shown[lane]) = (countsOf(lanes, lane), (-1, -1, -1, -1));
        (_section, _progress, _middle, _closing, _endedAt) = (_section + 1, -1, false, false, time);
        _traceTicks = 0;
    }

    private int territory(int lane) => _shown[lane] is var (great, good, _, _) && great >= 0 ? AiBattleRules.Territory(_course, great, good) : 0;

    // A side's counts; AI-don's 連打 is the player's (its rolls copy theirs: shown equal, user-asked).
    private static (int Great, int Good, int Miss, int Rolls) countsOf(IReadOnlyList<TaikoPlayResult> lanes, int lane) =>
        lane == 1 ? countsOf(lanes[1]) with { Rolls = countsOf(lanes[0]).Rolls } : countsOf(lanes[lane]);

    // A side's 良, 可, 不可 and 連打 (rolls and balloon hits) so far.
    private static (int Great, int Good, int Miss, int Rolls) countsOf(TaikoPlayResult play) => (play.Great, play.Good, play.Miss, play.Rolls + play.BalloonHits);

    private void both(string name, params LumenHostValue[] arguments)
    {
        call(_backdrop, name, arguments);
        call(_hitEffect, name, arguments);
    }

    // Diagnostic: WADDAMBURO_AI_TRACE=1 logs each call to the battle movies, and the hit effect's Don clips for 4 s after
    // each section's end.
    private static readonly bool Trace = Environment.GetEnvironmentVariable("WADDAMBURO_AI_TRACE") == "1";
    private static int _traceTicks;

    private static bool call(LumenPlayer? player, string name, params LumenHostValue[] arguments)
    {
        if (Trace && name is not ("SetProgress" or "SetPlayerSide" or "UpdateValue"))
            Console.WriteLine($"[ai] {name}({string.Join(", ", arguments.Select(static argument => argument.Kind == LumenHostValueKind.Number ? argument.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture) : argument.Kind.ToString()))})");
        return player?.TryInvokeCallback(name, arguments) == true;
    }

    private void traceDons()
    {
        if (!Trace || _hitEffect is null) return;
        _traceTicks++;
        if (_traceTicks % 4 == 0 && _section > 0 && _traceTicks <= 240)
            foreach (var line in _hitEffect.DescribeDisplayList().Where(static line => line.Contains("don", StringComparison.Ordinal)))
                Console.WriteLine($"[ai tree {_traceTicks}] {line}");
        if (_traceTicks % 4 == 0 && _section > 0 && _traceTicks <= 240)
            foreach (var quad in _hitEffect.CreateRenderSnapshot().Quads.Where(static quad => quad.NativeSurface is not null))
                Console.WriteLine($"[ai quad {_traceTicks}] {quad.NativeSurface} tl=({quad.TopLeft.X:0},{quad.TopLeft.Y:0}) br=({quad.BottomRight.X:0},{quad.BottomRight.Y:0})");
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);
}
