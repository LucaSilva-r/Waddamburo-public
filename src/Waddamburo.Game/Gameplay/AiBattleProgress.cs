using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// An AI battle through a song (traced session-ai-battle-3): its sections, each side's 良 / 可 / 不可 / 連打 this
/// section, the territory between them, who won each section, AI-don's mood and when the battle's result shows.
/// The engine's rules only: the game's movies are told by the skin's adapter, from the events (in the traced order).
/// </summary>
public sealed class AiBattleProgress(PlayableChart chart, TaikoCourse course)
{
    public const int MaxNeedle = 9;
    private static readonly TimeSpan ResultDelay = TimeSpan.FromSeconds(3); // traced: last section end -> result

    private readonly TimeSpan[] _ends = AiBattleRules.SectionEnds(chart);
    private readonly (int Great, int Good, int Miss, int Rolls)[] _base = new (int, int, int, int)[2];
    private readonly (int Great, int Good, int Miss, int Rolls)[] _shown = [(-1, -1, -1, -1), (-1, -1, -1, -1)];
    private readonly List<bool> _won = [];
    private readonly List<AiBattleSection> _sections = [];
    private bool _ready, _started, _middle, _closing, _resultShown;
    private int _section, _progress = -1;
    private TimeSpan _endedAt;

    /// <summary>Whether the presentation can be told things yet (the game's movies register their callbacks late).</summary>
    public Func<bool>? Ready { get; set; }

    public IReadOnlyList<bool> SectionsWon => _won;

    public AiBattleOutcome Outcome => Simulation?.Outcome ?? new([.. _sections]);

    public AiBattleSimulation? Simulation { get; set; }

    public int Sections => _ends.Length;

    public TimeSpan? ResultShownAt { get; private set; }

    public static readonly TimeSpan ResultsDelay = TimeSpan.FromSeconds(11.3);

    /// <summary>The territory needle: the player's territory less AI-don's, ±<see cref="MaxNeedle"/>.</summary>
    public int Needle => Math.Clamp(territory(0) - territory(1), -MaxNeedle, MaxNeedle);

    /// <summary>AI-don's mood: 0 (losing by two or more sections) to 4 (winning by two or more), 2 to start.</summary>
    public int Emotion { get; private set; } = 2;

    /// <summary>Whether the player won more than half the sections.</summary>
    public bool Won => _won.Count(static won => won) * 2 > _won.Count;

    /// <summary>The battle can be shown: its section count.</summary>
    public event Action<int>? Initialized;

    public event Action? Started;

    /// <summary>Once a tick after the start, before anything else that tick.</summary>
    public event Action? Ticked;

    /// <summary>The section's progress, percent.</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>A third (true) or two thirds (false) of the section have passed.</summary>
    public event Action<bool>? SectionCue;

    /// <summary>A side's counts this section changed: side (0 player, 1 AI-don) and its 良, 可, 不可, 連打.</summary>
    public event Action<int, (int Great, int Good, int Miss, int Rolls)>? CountsChanged;

    /// <summary>A section ended: whether the player won it, whether it was the last.</summary>
    public event Action<bool, bool>? SectionEnded;

    public event Action<int>? EmotionChanged;

    /// <summary>The battle's result shows: whether the player won.</summary>
    public event Action<bool>? ResultShown;

    public void Advance(TimeSpan time, IReadOnlyList<TaikoPlayResult> lanes)
    {
        if (!_ready)
        {
            if (Ready?.Invoke() == false)
                return;
            _ready = true;
            Initialized?.Invoke(_ends.Length);
        }
        if (!_started)
        {
            _started = true;
            Started?.Invoke();
        }
        Ticked?.Invoke();
        if (_section >= _ends.Length)
        {
            if (!_resultShown && time - _endedAt >= ResultDelay)
            {
                _resultShown = true;
                ResultShownAt = time;
                ResultShown?.Invoke(Won);
            }
            return;
        }
        var start = _section == 0 ? TimeSpan.Zero : _ends[_section - 1];
        var end = _ends[_section];
        var fraction = Math.Clamp((time - start) / (end - start), 0, 1);
        if ((int)(fraction * 100) is var progress && progress != _progress)
            ProgressChanged?.Invoke(_progress = progress);
        if (!_middle && fraction >= 1 / 3d)
        {
            _middle = true;
            SectionCue?.Invoke(true);
        }
        if (!_closing && fraction >= 2 / 3d)
        {
            _closing = true;
            SectionCue?.Invoke(false);
        }
        var simulated = Simulation?.GetSection(_section);
        for (var lane = 0; lane < Math.Min(2, lanes.Count); lane++)
        {
            var now = countsOf(lanes, lane);
            var counts = (now.Great - _base[lane].Great, now.Good - _base[lane].Good, now.Miss - _base[lane].Miss, now.Rolls - _base[lane].Rolls);
            if (simulated is { } snapshot)
            {
                var shown = lane == 0 ? snapshot.Player : snapshot.Ai;
                counts = (shown.Great, shown.Good, shown.Miss, shown.Rolls);
            }
            if (counts == _shown[lane])
                continue;
            _shown[lane] = counts;
            CountsChanged?.Invoke(lane, counts);
        }
        if (time < end || Simulation is { } simulation && simulation.CompletedSections <= _section)
            return;
        // A verified battle's section is the simulation's (it decides the winner), not what the lanes counted here.
        var won = simulated?.Won ?? territory(0) >= territory(1);
        _won.Add(won);
        AiSectionCounts section(int lane) => _shown[lane] is var (great, good, miss, rolls) && great >= 0
            ? new(great, good, miss, rolls) : default;
        _sections.Add(simulated ?? new(won, section(0), section(1)));
        SectionEnded?.Invoke(won, _section == _ends.Length - 1);
        var net = _won.Count(static section => section) * 2 - _won.Count;
        var emotion = net >= 2 ? 4 : net <= -2 ? 0 : Emotion;
        if (emotion != Emotion)
        {
            Emotion = emotion;
            EmotionChanged?.Invoke(emotion);
        }
        for (var lane = 0; lane < Math.Min(2, lanes.Count); lane++)
            (_base[lane], _shown[lane]) = (countsOf(lanes, lane), (-1, -1, -1, -1));
        (_section, _progress, _middle, _closing, _endedAt) = (_section + 1, -1, false, false, time);
    }

    private int territory(int lane) => _shown[lane] is var (great, good, _, _) && great >= 0 ? AiBattleRules.Territory(course, great, good) : 0;

    // A side's counts; AI-don's 連打 is the player's (its rolls copy theirs: shown equal, user-asked).
    private static (int Great, int Good, int Miss, int Rolls) countsOf(IReadOnlyList<TaikoPlayResult> lanes, int lane) =>
        lane == 1 ? countsOf(lanes[1]) with { Rolls = countsOf(lanes[0]).Rolls } : countsOf(lanes[lane]);

    // A side's 良, 可, 不可 and 連打 (rolls and balloon hits) so far.
    private static (int Great, int Good, int Miss, int Rolls) countsOf(TaikoPlayResult play) => (play.Great, play.Good, play.Miss, play.Rolls + play.BalloonHits);
}
