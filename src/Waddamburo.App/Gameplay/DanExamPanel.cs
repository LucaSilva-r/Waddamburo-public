using Waddamburo.Game.Dan;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// dani_enso's requirements panel (traced session30): SetChallengeLevel(level - 1), SetConditionNum(n),
/// SetConditionType(i, type, red), then as the play goes SetLeftNotes(n), SetConditionValue(i, value) and
/// SetConditionValid(i, 0|1). Its callbacks register on its first frame, so set up waits for them.
/// </summary>
/// <remarks>
/// The same is published to the provider (dan.rank, dan.song, dan.notesLeft, the dan.exam lists). A panel that reads
/// it is ours and gets none of the calls. ponytail: the game's panel is still called from here, not from the
/// gameplay adapter: no recorded dan session to prove a move against yet.
/// </remarks>
internal sealed class DanExamPanel(LumenPlayer movie, DanCourse course,
    Action<string, LumenHostValue> publish, Action<string, int, LumenHostValue> publishItem)
{
    private readonly DanExams _exams = new(course);
    private bool _ready;
    private DanProgress? _shown;
    private int _song;

    /// <summary>A requirement can no longer be met: the course ends with the song being played.</summary>
    public bool Failed { get; private set; }

    /// <summary>The play at song <paramref name="song"/>, begun at <paramref name="start"/>.</summary>
    public void Update(in DanProgress progress, int song, in DanProgress start)
    {
        if (!_ready && !(_ready = setUp()))
            return;
        if (_song != song)
        {
            _song = song;
            setTypes();
        }
        else if (_shown == progress)
            return;
        _shown = progress;
        call("SetLeftNotes", _exams.NotesLeft(progress));
        publish("dan.notesLeft", LumenHostValue.FromNumber(_exams.NotesLeft(progress)));
        foreach (var requirement in _exams.Requirements)
            Failed |= !_exams.StillPossible(requirement, progress, song, start);
        var shown = _exams.OnPanel;
        for (var index = 0; index < shown.Count; index++)
        {
            call("SetConditionValue", index, DanExams.ValueOf(shown[index], progress, start));
            call("SetConditionValid", index, _exams.StillPossible(shown[index], progress, song, start) ? 1 : 0);
            publishItem("dan.exam.value", index, LumenHostValue.FromNumber(DanExams.ValueOf(shown[index], progress, start)));
            publishItem("dan.exam.possible", index, LumenHostValue.FromBoolean(_exams.StillPossible(shown[index], progress, song, start)));
        }
    }

    /// <summary>The song's last note has gone: one of its own requirements not met ends the course with it.</summary>
    public void EndSong(in DanProgress progress, int song, in DanProgress start) =>
        Failed |= _exams.MissedOnSong(progress, song, start);

    private bool setUp()
    {
        // dani_enso shows its challenge plate (challenge_lv_mini) for LEVEL_CHALLENGE (101) and up; the ranks index
        // its label table (0 初級 ... 24 達人), anything past it leaves the first frame, 初級.
        if (!call("SetChallengeLevel", course.Rank == Waddamburo.Game.Dan.DanRanks.Challenge ? LevelChallenge : course.Rank))
            return false;
        call("SetConditionNum", _exams.OnPanel.Count);
        publish("dan.rank", LumenHostValue.FromNumber(course.Rank));
        publish("dan.name", LumenHostValue.FromString(course.Name));
        publish("dan.exam.count", LumenHostValue.FromNumber(_exams.OnPanel.Count));
        setTypes();
        return true;
    }

    private void setTypes()
    {
        var shown = _exams.OnPanel;
        publish("dan.song", LumenHostValue.FromNumber(_song + 1));
        for (var index = 0; index < shown.Count; index++)
        {
            publishItem("dan.exam.type", index, LumenHostValue.FromString(shown[index].Type));
            publishItem("dan.exam.target", index, LumenHostValue.FromNumber(DanExams.Target(shown[index].Red, _song)));
            publishItem("dan.exam.gold", index, LumenHostValue.FromNumber(shown[index].Gold.Length == 0 ? 0 : DanExams.Target(shown[index].Gold, _song)));
        }
        for (var index = 0; index < shown.Count; index++)
            call("SetConditionType", index, DanExams.TypeOf(shown[index].Type), DanExams.Target(shown[index].Red, _song));
    }

    private const int LevelChallenge = 101;

    private bool call(string name, params long[] arguments) =>
        movie.UsesProvider || movie.TryInvokeCallback(name, [.. arguments.Select(static value => LumenHostValue.FromNumber(value))]);
}
