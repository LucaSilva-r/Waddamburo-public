using Waddamburo.Game.Dan;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// dani_enso's requirements panel (traced session30): SetChallengeLevel(level - 1), SetConditionNum(n),
/// SetConditionType(i, type, red), then as the play goes SetLeftNotes(n), SetConditionValue(i, value) and
/// SetConditionValid(i, 0|1). Its callbacks register on its first frame, so set up waits for them.
/// </summary>
internal sealed class DanExamPanel(LumenPlayer movie, DanCourse course)
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
        foreach (var requirement in _exams.Requirements)
            Failed |= !_exams.StillPossible(requirement, progress, song, start);
        var shown = _exams.OnPanel;
        for (var index = 0; index < shown.Count; index++)
        {
            call("SetConditionValue", index, DanExams.ValueOf(shown[index], progress, start));
            call("SetConditionValid", index, _exams.StillPossible(shown[index], progress, song, start) ? 1 : 0);
        }
    }

    /// <summary>The song's last note has gone: one of its own requirements not met ends the course with it.</summary>
    public void EndSong(in DanProgress progress, int song, in DanProgress start) =>
        Failed |= _exams.MissedOnSong(progress, song, start);

    private bool setUp()
    {
        if (!call("SetChallengeLevel", course.Rank))
            return false;
        call("SetConditionNum", _exams.OnPanel.Count);
        setTypes();
        return true;
    }

    private void setTypes()
    {
        var shown = _exams.OnPanel;
        for (var index = 0; index < shown.Count; index++)
            call("SetConditionType", index, DanExams.TypeOf(shown[index].Type), DanExams.Target(shown[index].Red, _song));
    }

    private bool call(string name, params long[] arguments) =>
        movie.TryInvokeCallback(name, [.. arguments.Select(static value => LumenHostValue.FromNumber(value))]);
}
