using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// Optional fast scrolling of the song select list, with the movie unchanged. The movie moves one
/// board per rim hit with an eased slide of <see cref="SlideFrames"/> frames and reads the next hit
/// only once the slide is over (about six boards a second), dropping the hits in between. Here:
/// <list type="bullet">
/// <item>Rim hits during a slide, and the mouse wheel's notches, are queued. While steps are queued the
/// list clip is run ahead so its slide takes fewer ticks at an even speed, and the next hit is handed to
/// the movie as soon as it reads input again.</item>
/// <item>Page Up/Down skip ten boards inside a folder (three among the folders): the movie is advanced
/// through the steps within the tick.</item>
/// <item>Three quick rim hits one way start skipping, as the later games do: from then each rim hit
/// skips its way, a short while apart at the least, until the drum is left alone.</item>
/// </list>
/// Nothing in the movie is written to: it sees the same hits and plays the same frames, sooner. A single
/// hit on a list at rest is left to the movie as it is. Queued steps and skips inside a folder stop at
/// its last song rather than run out of it.
/// </summary>
/// <param name="movie">The song select movie.</param>
/// <param name="openFolder">The open folder's genre index, or -1 when none is open.</param>
/// <param name="jumped">A skip was taken (by this player, 0 or 1): its sound is due.</param>
public sealed class SongSelectScroll(LumenPlayer movie, Func<int> openFolder, Action<int> jumped)
{
    // The list clip and its move tweens' labels.
    private const string List = "main/genreSelect_";
    private const string RightLabel = "カーソル右", LeftLabel = "カーソル左";
    // From a move label: the boards slide for this many frames (the movie reads input again from the
    // frame after), then the centre board opens.
    private const int SlideFrames = 8, MoveFrames = 24;
    private const int PageSteps = 10, FolderPageSteps = 3;
    // A queued step not taken for this long is dropped (the movie is doing something else).
    private const int PatienceTicks = 30;
    // No new hit for this long: the queue is cut to one step, so the list stops with the wheel.
    private const int MomentumTicks = 5;
    // The list's boards as the movie holds them (the centre one's place is BoardContainer.INDEX_CENTER).
    private const string Boards = "main.genreSelect_.container.board", CentreIndex = "_global.BoardContainer.INDEX_CENTER";
    // Flash key codes the movie polls: each player's left rim, right rim and centre.
    private static readonly int[] LeftKeys = ['A', 'D'], RightKeys = ['S', 'F'], DecideKeys = ['Z', 'C'];
    // Page Up/Down, and the platform's codes for a wheel notch up and down.
    private const int PageUp = 33, PageDown = 34, WheelUp = 1001, WheelDown = 1002;
    // Rim hits one way, each within SkipHitTicks of the last, that start skipping. While skipping a rim
    // hit skips its way, but not within SkipCooldownTicks of the last skip (those hits do nothing); no
    // hit for SkipIdleTicks ends it.
    private const int SkipHits = 3, SkipHitTicks = 6, SkipCooldownTicks = 15, SkipIdleTicks = 45;

    /// <summary>A skip is running the movie through its steps: their rim hits are not to be heard one by one.</summary>
    public bool Jumping { get; private set; }

    private int _right = -1, _left = -1;
    // Steps still to take: positive to the right.
    private int _pending;
    private int _player;
    // Ticks since the movie took the current step; ticks a queued step has waited.
    private int _age, _waited, _sinceHit;
    // Rim hits in a row one way (_streakWay), and ticks since the last; skipping, and ticks since a skip.
    private int _streak, _streakWay, _sinceRim, _sinceSkip;
    private bool _skipping;
    // The step handed to the movie this tick (0: none); the list was busy with another animation then.
    private int _handed;
    private bool _busy;

    // Ticks a slide takes with this many steps queued behind it; the movie then needs one more tick to
    // take the next hit: 30, 20, 15 and 12 boards a second.
    private static int slideTicks(int queued) => queued >= 8 ? 1 : queued >= 4 ? 2 : queued >= 2 ? 3 : 4;

    /// <summary>
    /// Before the movie advances: queues the tick's hits, runs a slide ahead while steps are queued, and
    /// hands the movie the next step when it can take it.
    /// </summary>
    /// <param name="input">The tick's input for the movie.</param>
    /// <param name="listActive">The list has the input (false: the difficulty selector is open).</param>
    public LumenInputSnapshot Before(LumenInputSnapshot input, bool listActive)
    {
        ArgumentNullException.ThrowIfNull(input);
        _handed = 0;
        var keys = input.PressedKeyCodes;
        // A centre hit picks what is under the cursor: the queue ends there.
        if (!listActive || DecideKeys.Any(keys.Contains) || !resolve() || movie.InstanceFrame(List) is not { } list)
        {
            _pending = _streak = 0;
            _skipping = false;
            return input;
        }
        var slide = list.Playing ? slideFrame(list.Frame) : -1;
        _busy = list.Playing && slide < 0;
        if (slide >= 0)
            _age++;
        var rim = 0;
        for (var player = 0; player < LeftKeys.Length; player++)
        {
            var hit = (keys.Contains(RightKeys[player]) ? 1 : 0) - (keys.Contains(LeftKeys[player]) ? 1 : 0);
            if (hit == 0)
                continue;
            _player = player;
            rim = hit;
        }
        var wheel = (keys.Contains(WheelDown) ? 1 : 0) - (keys.Contains(WheelUp) ? 1 : 0);
        var page = (keys.Contains(PageDown) ? 1 : 0) - (keys.Contains(PageUp) ? 1 : 0);
        _sinceSkip++;
        if (++_sinceRim > SkipIdleTicks)
            _skipping = false;
        if (rim != 0 && _skipping)
        {
            _sinceRim = 0;
            // Too soon after the last skip: the hit is dropped.
            if (_sinceSkip < SkipCooldownTicks)
                return new LumenInputSnapshot(keys.Except(LeftKeys).Except(RightKeys));
            page = rim;
        }
        else if (rim != 0)
        {
            _streak = rim == _streakWay && _sinceRim <= SkipHitTicks ? _streak + 1 : 1;
            _streakWay = rim;
            _sinceRim = 0;
            if (_streak >= SkipHits)
            {
                _skipping = true;
                _streak = 0;
                page = rim;
            }
        }
        if (page != 0 && !_busy)
        {
            if (rim != 0)
                _sinceSkip = 0;
            _pending = 0;
            jump(page);
            return new LumenInputSnapshot(keys.Except(LeftKeys).Except(RightKeys));
        }
        if (_pending == 0 && wheel == 0 && slide < 0)
            return input;
        _sinceHit++;
        if (rim + wheel != 0)
        {
            _pending += rim + wheel;
            _sinceHit = 0;
        }
        if (_sinceHit > MomentumTicks)
            _pending = Math.Clamp(_pending, -1, 1);
        input = new LumenInputSnapshot(keys.Except(LeftKeys).Except(RightKeys));
        if (_pending == 0)
            return input;
        if (slide >= 0 && slide < SlideFrames)
        {
            // The slide eases out (x = 1 - (1 - f)^2): the frame as far along as the step's time is.
            var progress = Math.Min(1d, (double)_age / slideTicks(Math.Abs(_pending)));
            var frame = (int)Math.Round(SlideFrames * (1 - Math.Sqrt(1 - progress)));
            movie.TryFastForward(List, list.Frame - slide + frame);
            return input;
        }
        // The slide is over, or the list is at rest or in another animation (a folder opening): the hit
        // is offered every tick until the movie takes it, but not for ever.
        if (++_waited > PatienceTicks || atFolderEdge(Math.Sign(_pending)))
        {
            _pending = 0;
            return input;
        }
        _handed = Math.Sign(_pending);
        return new LumenInputSnapshot(input.PressedKeyCodes.Add(_handed > 0 ? RightKeys[_player] : LeftKeys[_player]));
    }

    /// <summary>After the movie advanced: notes the step it took.</summary>
    public void After()
    {
        if (_right < 0 || movie.InstanceFrame(List) is not { } list)
            return;
        var slide = list.Playing ? slideFrame(list.Frame) : -1;
        // The tween just started: the movie took a step (ours, or a hit it read itself).
        if (slide == 0)
        {
            _age = _waited = 0;
            _pending -= _handed;
        }
        // Our hit closed the folder instead (the list's edge): the rest of the queue goes.
        else if (_handed != 0 && !_busy && list.Playing && slide < 0)
            _pending = 0;
    }

    // Skips a page of steps at once: the movie is advanced with a rim hit until it has taken them all
    // (it reads a hit the tick after a slide's end), each slide run to its end first. The last one
    // plays out. From inside a folder the jump ends at its last board; from that board, or among the
    // folders, it is a short one that may leave.
    private void jump(int direction)
    {
        var contained = genre(0) == openFolder() && !atFolderEdge(direction);
        var steps = contained ? PageSteps : FolderPageSteps;
        var hit = new LumenInputSnapshot([direction > 0 ? RightKeys[_player] : LeftKeys[_player]]);
        var taken = 0;
        Jumping = true;
        // ponytail: a bounded number of advances; a step among the folders needs more of them than one between songs.
        for (var advances = 0; taken < steps && advances < steps * 12; advances++)
        {
            if (movie.InstanceFrame(List) is not { } list)
                break;
            var slide = list.Playing ? slideFrame(list.Frame) : -1;
            if (list.Playing && slide < 0 || contained && atFolderEdge(direction))
                break;
            if (slide >= 0 && slide < SlideFrames)
                movie.TryFastForward(List, list.Frame - slide + SlideFrames);
            movie.Advance(hit);
            if (movie.InstanceFrame(List) is { Playing: true } after && slideFrame(after.Frame) == 0)
                taken++;
        }
        Jumping = false;
        _age = 0;
        if (taken > 0)
            jumped(_player);
    }

    // The cursor is in the open folder and the next board that way is not: a step there would leave it.
    private bool atFolderEdge(int direction) =>
        openFolder() is >= 0 and var open && genre(0) == open && genre(direction) is { } next && next != open;

    // The genre of the board this many places from the one the cursor is going to. The boards only take
    // a step's new songs when its tween is finished: until then that one is still a board to the side.
    private int? genre(int offset)
    {
        if (movie.InstanceFrame(List) is not { } list || number(movie.ReadScriptValue(CentreIndex)) is not { } centre)
            return null;
        var moving = !list.Playing || slideFrame(list.Frame) < 0 ? 0 : list.Frame >= _right && list.Frame < _right + MoveFrames ? 1 : -1;
        return number(movie.ReadScriptValue($"{Boards}.{centre + moving + offset}.musicInfo.genreIndex"));

        static int? number(object? value) => value is IConvertible and not string and not bool
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    // Frames into a move tween (0 at its label), or -1 outside both.
    private int slideFrame(int frame) =>
        frame >= _right && frame < _right + MoveFrames ? frame - _right
        : frame >= _left && frame < _left + MoveFrames ? frame - _left : -1;

    // The tweens' frames, looked up once the list exists; false for a movie without them.
    private bool resolve()
    {
        if (_right < 0 && !(movie.TryGetLabelFrame(List, RightLabel, out _right) && movie.TryGetLabelFrame(List, LeftLabel, out _left)))
            _right = -1;
        return _right >= 0;
    }
}
