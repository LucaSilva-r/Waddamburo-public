using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>One board of dani_select: a dan id (see <see cref="DaniSelectHostBinding"/>) and its songs.</summary>
public sealed record DaniSelectBoard(int Id, bool Valid, IReadOnlyList<DaniSelectSong> Songs, int Notes = 0,
    IReadOnlyList<DaniSelectCondition>? Conditions = null);

/// <summary>A requirement on a board's panel: <paramref name="Type"/> is ConditionInfo's (see <see cref="DaniSelectHostBinding"/>).</summary>
public sealed record DaniSelectCondition(int Type, int Value);

/// <summary>A board's song: <paramref name="Uid"/> comes back in RequestFillrect to ask for its title.</summary>
public sealed record DaniSelectSong(int Uid, int Genre, int Course, int Star);

/// <summary>
/// dani_select.lm, the dan dojo's course list (traced session30). The movie asks with Lumen.InitData; the game
/// answers before IsInitWait says yes (null boards: not known yet, keep waiting): SetPlayer(player, selected, current), then per board AssignDani(id),
/// SetDaniStatus(valid, onpu, history) and AssignMusic(uid, genre, course, star) per song. Ids are
/// DaniInfo's: 0 初級 .. 24 達人, 30 the back board. An invalid board shows locked and cannot be picked.
/// AssignCondition(type, value) fills the board's requirements panel; ConditionInfo's types are 0 gauge,
/// 1 goods, 2 oks, 3 bads, 4 combo, 5 hits, 6 score, 7 rolls.
/// The movie shows a song's title in clip fillrect_(type), asked through RequestFillrect(type, uid). The pick
/// comes as SetSelectedInfo(board index), then LumenTerminate.
/// </summary>
public sealed class DaniSelectHostBinding(
    Func<IReadOnlyList<DaniSelectBoard>?> boards,
    int side,
    Func<int, LumenNativeSurfaceKey?> title,
    ISongSelectSoundController? sounds = null,
    IDonPresentationController? don = null,
    Action<string, int>? playCue = null) : ILumenHostBinding
{
    /// <summary>DaniInfo.ID_BACK: the board that returns to Song Select.</summary>
    public const int BackId = 30;

    // current = every rank open: the movie hides the top ranks until the one below is passed, a rule of
    // Sorairo's that this game does not keep.
    private const int EveryRankOpen = 25;

    private LumenPlayer? _player;
    private IReadOnlyList<DaniSelectBoard>? _boards;
    private bool _boardsJumped;

    /// <summary>The picked board's index, once the movie reported it.</summary>
    public int? Selected { get; private set; }

    public bool Terminated { get; private set; }

    /// <summary>Back to Song Select: the back board was picked.</summary>
    public bool Back => Selected is { } index && _boards is { } shown
        && ((uint)index >= (uint)shown.Count || shown[index].Id == BackId);

    /// <summary>After the tick's movie advance, once the boards are where the tick leaves them.</summary>
    public void Poll()
    {
        if (_boardsJumped)
            _player?.HoldStill();
        _boardsJumped = false;
    }

    public void Attach(LumenPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        if (don is not null)
            DonLumenBinding.Attach(player, don, DonPresentationLayout.OpposedPlayers);
    }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(_ => LumenHostValue.Undefined);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("InitData", static _ => LumenHostValue.FromBoolean(true));
            // Polled, so its callbacks exist by now (the first frame registers them).
            lumen.RegisterMethod("IsInitWait", _ =>
            {
                assign();
                return LumenHostValue.FromBoolean(_boards is not null);
            });
            lumen.RegisterMethod("IsReady", static _ => LumenHostValue.FromBoolean(true));
            // ponytail: no countdown here yet (the movie asks for 100 s); add it with the arcade series.
            lumen.RegisterMethod("StartTimer", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("StopTimer", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("ResumeTimer", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("GetTimeSec", static _ => LumenHostValue.FromNumber(100));
            lumen.RegisterMethod("SetIndicator", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("IsTimeup", static _ => LumenHostValue.FromBoolean(false));
            lumen.RegisterMethod("NotifyDaniSelect", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("NotifyConfirm", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("RequestFillrect", call =>
            {
                if (number(call, 0) is { } type && number(call, 1) is { } uid && title(uid) is { } surface)
                    _player?.SetNativeFill($"fillrect_{type}", surface);
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("RequestSE", call =>
            {
                sounds?.RequestSound(new SongSelectSoundRequest(SongSelectSoundRequestKind.Effect, call.Arguments));
                return LumenHostValue.Undefined;
            });
            // Traced (player, kind) -> SE_COM: 1 the rim (46 while scrolling), 0 the centre, 2 the start.
            lumen.RegisterMethod("RequestTaikoSE", call =>
            {
                if (number(call, 1) is { } kind)
                    playCue?.Invoke("SE_COM", kind switch { 1 => 3, 2 => 6, _ => 0 });
                return LumenHostValue.Undefined;
            });
            // ponytail: RequestSE_Play reads a course's name aloud (traced bank 83, ids 9-11): silent for now.
            lumen.RegisterMethod("RequestSE_Play", static _ => LumenHostValue.Undefined);
            lumen.RegisterMethod("RequestSE_Stop", static _ => LumenHostValue.Undefined);
            if (don is not null)
                DonLumenBinding.RegisterMotion(context, lumen, don);
            lumen.RegisterMethod("SetSelectedInfo", call =>
            {
                Selected = number(call, 0);
                return LumenHostValue.FromBoolean(true);
            });
            lumen.RegisterMethod("LumenTerminate", _ =>
            {
                Terminated = true;
                return LumenHostValue.FromBoolean(true);
            });
        });
    }

    private void assign()
    {
        if (_boards is not null || _player is null || boards() is not { } ready)
            return;
        _boards = ready;
        // The boards recycle: stopping puts each back on its slot, a 125 px jump with new content, under the
        // distance interpolation takes for a cut. Drawn as is, or they slide across it (a flicker at high refresh).
        if (!_player.TryWrapScriptMethod("_global.BoardContainer.prototype", "ResetBoards", (_, original) =>
        {
            _boardsJumped = true;
            return original();
        }))
            Console.Error.WriteLine("dani_select has no BoardContainer.ResetBoards to hold still.");
        invoke("SetPlayer", side, firstValid(ready) + 1, EveryRankOpen);
        foreach (var board in ready)
        {
            invoke("AssignDani", board.Id);
            _player.TryInvokeCallback("SetDaniStatus", [LumenHostValue.FromBoolean(board.Valid),
                LumenHostValue.FromNumber(board.Notes), LumenHostValue.FromNumber(0)]);
            foreach (var song in board.Songs)
                invoke("AssignMusic", song.Uid, song.Genre, song.Course, song.Star);
            foreach (var condition in board.Conditions ?? [])
                invoke("AssignCondition", condition.Type, condition.Value);
        }
    }

    private static int firstValid(IReadOnlyList<DaniSelectBoard> boards)
    {
        for (var index = 0; index < boards.Count; index++)
            if (boards[index].Valid && boards[index].Id != BackId)
                return index - 1;
        return 0;
    }

    private void invoke(string name, params int[] arguments) =>
        _player?.TryInvokeCallback(name, [.. arguments.Select(static value => LumenHostValue.FromNumber(value))]);

    private static int? number(LumenHostCall call, int index) =>
        (uint)index < (uint)call.Arguments.Length && call.Arguments[index].Kind == LumenHostValueKind.Number
            ? (int)call.Arguments[index].AsNumber() : null;
}
