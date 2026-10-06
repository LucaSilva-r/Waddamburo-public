using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// Training (home): a song practised on a <see cref="ReviewSession"/>, played live from the start. The
/// replay keys work as it plays: held arrows scrub, the wheel and Shift+arrows (note to note) jump, up/down
/// change the speed, Space pauses. While the playhead is moved the lanes show the attempt so far (what was
/// hit); let go and the drum plays live from there. A/B mark a loop: at its end play starts again from A
/// with a short lead-in, or (L) stops on the attempt. Nothing is saved.
/// </summary>
internal sealed class TrainingSession
{
    // Real time played before the loop's start, to catch the rhythm.
    private static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(2);

    private readonly GameShell _shell;
    private readonly ReviewSession _review;
    private readonly TimeSpan[] _notes;
    // Where the live lanes started judging (the attempt so far runs from there).
    private TimeSpan _liveFrom;

    public TrainingSession(GameShell shell, ReviewSession review, PlayableChart chart)
    {
        _shell = shell;
        _review = review;
        _notes = [.. chart.HitObjects.Select(static note => note.StartTime)
            .Concat(chart.LongNotes.Select(static note => note.StartTime)).Distinct().Order()];
    }

    public ReviewClock Clock => _review.Clock;

    /// <summary>The loop's start and end (chart time; null: the song's start, its end).</summary>
    public TimeSpan? LoopStart { get; private set; }

    public TimeSpan? LoopEnd { get; private set; }

    /// <summary>The lanes are played live (the drum is judged); false while the attempt is shown after a move.</summary>
    public bool Live { get; private set; }

    /// <summary>At the loop's end: stop on the attempt rather than play the loop again.</summary>
    public bool PauseAfterAttempt { get; private set; }

    /// <summary>The last finished loop's Good/OK/Bad counts (null before the first).</summary>
    public (int Great, int Good, int Miss)? LastAttempt { get; private set; }

    /// <summary>
    /// The lanes as practice starts: live from the song's beginning, or paused at <paramref name="at"/> (a
    /// replay turned into practice; Space plays on from there).
    /// </summary>
    public void Begin(TimeSpan? at = null)
    {
        goLive(at ?? Clock.Start);
        if (at is not null)
            Clock.SetPaused(true);
    }

    /// <summary>One display frame (<paramref name="keysEnabled"/> false: a menu has the keys).</summary>
    public void Frame(SdlKeyboardSnapshot keys, bool keysEnabled)
    {
        var scrub = 0;
        if (keysEnabled)
        {
            var jump = GameActions.Down(keys, GameAction.NoteJump); // held: the arrows go note to note
            bool pressed(GameAction action) => GameActions.Pressed(keys, action);
            foreach (var press in keys.Presses)
                switch (press.Key)
                {
                    case SdlKeyboardKey.WheelUp: this.jump(Clock.Position - TimeSpan.FromSeconds(1)); break;
                    case SdlKeyboardKey.WheelDown: this.jump(Clock.Position + TimeSpan.FromSeconds(1)); break;
                    case SdlKeyboardKey.Left when jump: this.jump(note(-1)); break;
                    case SdlKeyboardKey.Right when jump: this.jump(note(+1)); break;
                    case SdlKeyboardKey.Up: Clock.Faster(); break;
                    case SdlKeyboardKey.Down: Clock.Slower(); break;
                }
            if (pressed(GameAction.Pause))
                Clock.TogglePause();
            // The loop's two keys together clear it.
            if (pressed(GameAction.LoopStart) && GameActions.Down(keys, GameAction.LoopEnd)
                || pressed(GameAction.LoopEnd) && GameActions.Down(keys, GameAction.LoopStart))
                (LoopStart, LoopEnd) = (null, null);
            else if (pressed(GameAction.LoopStart))
            {
                LoopStart = Clock.Position;
                if (LoopEnd <= LoopStart) LoopEnd = null;
            }
            else if (pressed(GameAction.LoopEnd))
            {
                LoopEnd = Clock.Position;
                if (LoopStart >= LoopEnd) LoopStart = null;
            }
            if (pressed(GameAction.AfterPass))
                PauseAfterAttempt = !PauseAfterAttempt;
            if (!jump)
                scrub = (keys.IsDown(SdlKeyboardKey.Right) ? 1 : 0) - (keys.IsDown(SdlKeyboardKey.Left) ? 1 : 0);
        }
        if (scrub != 0)
            showAttempt();
        else if (!Live && !Clock.Paused)
            goLive(Clock.Position); // let go (or resumed after a move): the drum plays on from here
        // Paused, the live lanes just stand (a hit would be judged at a frozen time).
        _review.Step(scrub, Live && !Clock.Paused ? keys : null);
        if (Live && Clock.Position >= (LoopEnd ?? Clock.End))
            loopEnded();
    }

    /// <summary>The flow is paused (its menu is open): see <see cref="ReviewSession.Hold"/>.</summary>
    public void Hold() => _review.Hold();

    private void jump(TimeSpan to)
    {
        showAttempt();
        Clock.Seek(to);
    }

    // The loop is over: again from its start (with a lead-in), or stopped on the attempt.
    private void loopEnded()
    {
        var result = _shell.Gameplay.Results[0];
        LastAttempt = (result.Great, result.Good, result.Miss);
        if (PauseAfterAttempt || LoopEnd is null)
        {
            Clock.SetPaused(true);
            showAttempt();
            return;
        }
        var from = (LoopStart ?? Clock.Start) - LeadIn * Clock.Speed;
        goLive(from < Clock.Start ? Clock.Start : from);
    }

    private void goLive(TimeSpan from)
    {
        _review.StartLive(from);
        _liveFrom = from;
        Live = true;
    }

    // The live lanes give way to the attempt so far, baked, so moving the playhead shows what was played.
    private void showAttempt()
    {
        if (!Live)
            return;
        Live = false;
        _review.ShowAttempt([.. _shell.Gameplay.Replays.Select(static replay => (IReadOnlyList<TaikoReplayInput>)[.. replay.Inputs])],
            _liveFrom, Clock.Position);
    }

    // The next note's time after the playhead (direction +1) or the one before it (-1); a note just reached counts as passed.
    private TimeSpan note(int direction)
    {
        var position = Clock.Position;
        var epsilon = TimeSpan.FromMilliseconds(1);
        return direction > 0
            ? _notes.FirstOrDefault(time => time > position + epsilon, Clock.End)
            : _notes.LastOrDefault(time => time < position - epsilon, Clock.Start);
    }
}
