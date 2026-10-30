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

    /// <summary>A requirement can no longer be met: the course ends with the song being played.</summary>
    public bool Failed { get; private set; }

    public void Update(in DanProgress progress)
    {
        if (!_ready && !(_ready = setUp()))
            return;
        if (_shown == progress)
            return;
        _shown = progress;
        call("SetLeftNotes", _exams.NotesLeft(progress));
        for (var index = 0; index < _exams.Requirements.Count; index++)
        {
            var requirement = _exams.Requirements[index];
            call("SetConditionValue", index, DanExams.ValueOf(requirement.Type, progress));
            var possible = _exams.StillPossible(requirement, progress);
            Failed |= !possible;
            call("SetConditionValid", index, possible ? 1 : 0);
        }
    }

    private bool setUp()
    {
        if (!call("SetChallengeLevel", course.Level - 1))
            return false;
        call("SetConditionNum", _exams.Requirements.Count);
        for (var index = 0; index < _exams.Requirements.Count; index++)
            call("SetConditionType", index, DanExams.TypeOf(_exams.Requirements[index].Type), _exams.Requirements[index].Red[0]);
        return true;
    }

    private bool call(string name, params long[] arguments) =>
        movie.TryInvokeCallback(name, [.. arguments.Select(static value => LumenHostValue.FromNumber(value))]);
}
