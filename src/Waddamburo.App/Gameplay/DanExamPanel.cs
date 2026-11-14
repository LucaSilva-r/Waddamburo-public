using Waddamburo.Game.Dan;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// A dan course's requirements as the play goes: published to the provider (dan.rank, dan.name, dan.song,
/// dan.notesLeft, the dan.exam lists), and whether the course has failed. The game's panel is driven from what is
/// published, by the gameplay adapter (<see cref="GameplayGameMovies"/>).
/// </summary>
internal sealed class DanExamPanel(DanCourse course,
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
        if (!_ready)
        {
            _ready = true;
            publish("dan.rank", LumenHostValue.FromNumber(course.Rank));
            publish("dan.name", LumenHostValue.FromString(course.Name));
            publish("dan.exam.count", LumenHostValue.FromNumber(_exams.OnPanel.Count));
            setTypes();
        }
        if (_song != song)
        {
            _song = song;
            setTypes();
        }
        else if (_shown == progress)
            return;
        _shown = progress;
        publish("dan.notesLeft", LumenHostValue.FromNumber(_exams.NotesLeft(progress)));
        foreach (var requirement in _exams.Requirements)
            Failed |= !_exams.StillPossible(requirement, progress, song, start);
        var shown = _exams.OnPanel;
        for (var index = 0; index < shown.Count; index++)
        {
            publishItem("dan.exam.value", index, LumenHostValue.FromNumber(DanExams.ValueOf(shown[index], progress, start)));
            publishItem("dan.exam.possible", index, LumenHostValue.FromBoolean(_exams.StillPossible(shown[index], progress, song, start)));
        }
    }

    /// <summary>The song's last note has gone: one of its own requirements not met ends the course with it.</summary>
    public void EndSong(in DanProgress progress, int song, in DanProgress start) =>
        Failed |= _exams.MissedOnSong(progress, song, start);

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
    }
}
