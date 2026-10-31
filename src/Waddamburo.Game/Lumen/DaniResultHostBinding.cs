using Waddamburo.Game.Dan;
using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// A dan course's results (dani_result, traced sessions 30 and 31): the play's numbers once its callbacks exist,
/// then the movie animates itself, asking for Don's motions and sounds, and ends with _global.isAllEnd.
/// There is no sending to a server to wait for, so WAIT_SEND_SERVER and WAKEUP_SEND_SERVER do nothing.
/// </summary>
public sealed class DaniResultHostBinding(DanCourseResult result, IDonPresentationController? don, Action<string, int>? playCue) : ILumenHostBinding
{
    private static readonly string[] Ignored = ["SOUND_STOP_ALL", "CHANGE_DANI_PLATE", "REQUEST_FILLRECT", "WAIT_SEND_SERVER",
        "WAKEUP_SEND_SERVER", "REQUEST_BGM_STOP", "REQUEST_BGM_STAFFROLL"];

    private LumenPlayer? _player;
    private bool _sent;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", static _ => { });
        context.RegisterExternalInterfaceCall(call =>
        {
            if (call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Text
                && call.Arguments[0].AsString() == "DonMot" && don is not null)
                DonLumenBinding.ApplyMotion(context, don, call.Arguments[1..], ResultHostBinding.ResultMotionName);
            return LumenHostValue.Undefined;
        });
        context.RegisterObject("LumenMethod", method =>
        {
            method.RegisterMethod("SE_REQUEST", call => play(call, effect: true));
            method.RegisterMethod("VOICE_REQUEST", call => play(call, effect: false));
            method.RegisterMethod("IS_END_CHANGE_COSTUME", static _ => LumenHostValue.FromBoolean(true));
            foreach (var ignored in Ignored)
                method.RegisterMethod(ignored, static _ => LumenHostValue.Undefined);
        });
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        if (don is not null)
            DonLumenBinding.Attach(player, don);
        Poll();
    }

    /// <summary>Sends the numbers as soon as the movie's first frame has registered its callbacks.</summary>
    public void Poll()
    {
        if (_sent || _player is not { } player || !call(player, "SetPlayerEntry", 0))
            return;
        _sent = true;
        var total = result.Total;
        // ponytail: the player's own rank is not kept yet, and plates do not exist (nameplates are free text).
        call(player, "SetDaniCurrent", result.Course.Level);
        call(player, "SetSongPlayCount", result.SongsPlayed);
        for (var index = 0; index < result.Songs.Count; index++)
        {
            var song = result.Songs[index];
            call(player, "SetGenre", index, song.Genre);
            call(player, "SetCourse", index, song.Course);
            call(player, "SetStar", index, song.Level);
        }
        for (var index = 0; index < result.Songs.Count; index++)
        {
            var counts = result.Songs[index].Counts;
            call(player, "SetRyo", index, counts.Goods);
            call(player, "SetKa", index, counts.Oks);
            call(player, "SetFuka", index, counts.Bads);
            call(player, "SetRenda", index, counts.Rolls);
        }
        call(player, "SetScore", total.Score);
        call(player, "SetCombo", total.MaxCombo);
        call(player, "SetBeats", total.Hits);
        call(player, "SetGauge", total.GaugePercent / 2);
        call(player, "SetDaniLevel", result.Course.Rank + 1);
        call(player, "SetMedleyCourse", 3);
        var exams = new DanExams(result.Course).OnResults(total, result.SongEnds);
        for (var index = 0; index < exams.Count; index++)
        {
            call(player, "SetAchieveType", index, DanExams.TypeOf(exams[index].Type));
            // A per-song requirement has no one value here: the songs' together, against the course's count.
            call(player, "SetAchieveSuccess", index, exams[index].Red.Sum());
            call(player, "SetAchieve", index, DanExams.ValueOf(exams[index].Type, total));
        }
        call(player, "SetAchieveCount", exams.Count);
        player.TryInvokeCallback("SetLevelUp", [LumenHostValue.FromBoolean(false)]);
        player.TryInvokeCallback("SetDaniPlate", [LumenHostValue.FromBoolean(false)]);
        call(player, "SetResultLevel", result.Level);
        call(player, "SetPlayerForPlayerName", 0, 0);
        player.TryInvokeCallback("SetTitleForPlayerName", [LumenHostValue.FromString(TaikoGuest.Title(0)), LumenHostValue.FromNumber(0)]);
        call(player, "SetTitlePanelForPlayerName", TaikoGuest.TitlePlate(0), 0);
        player.TryInvokeCallback("SetDaniForPlayerName", [LumenHostValue.FromNumber(0), LumenHostValue.FromBoolean(false), LumenHostValue.FromNumber(0)]);
        call(player, "SetNameSizeForPlayerName", 0, 0);
        player.TryInvokeCallback("ApplyForPlayerName", []);
        NameText.Show(player, "main/name_board/names", TaikoGuest.Name(0));
    }

    private LumenHostValue play(LumenHostCall call, bool effect)
    {
        if (playCue is not null && call.Arguments.Length > 1 && call.Arguments[1].Kind == LumenHostValueKind.Number)
        {
            var id = (int)call.Arguments[1].AsNumber();
            if (Cue(effect, id) is { } cue)
                playCue(cue.Bank, cue.Index);
            else
                Console.WriteLine($"dani_result asked for untraced {(effect ? "effect" : "voice")} {id}.");
        }
        return LumenHostValue.Undefined;
    }

    /// <summary>The sound a request plays, where a trace showed it.</summary>
    public static (string Bank, int Index)? Cue(bool effect, int id) => (effect, id) switch
    {
        (true, 0) => ("SE_TOTAL", 36),
        (true, 1) => ("SE_TOTAL", 38),
        // ponytail: SE_WAIT and SE_COM_TAIKO_DON_C, by their names and neighbours; both traced runs skipped past them.
        (true, 2) => ("SE_TOTAL", 39),
        (true, 26) => ("SE_COM", 0),
        (true, 3) => ("SE_TOTAL", 40),
        (true, 4) => ("SE_TOTAL", 44),
        (true, 5) => ("SE_TOTAL", 45),
        (true, 6) => ("SE_TOTAL", 46),
        (true, 7) => ("SE_TOTAL", 47),
        (true, 8) => ("SE_TOTAL", 48),
        (true, 9) => ("SE_TOTAL", 50),
        (true, 11) => ("SE_TOTAL", 56),
        (true, 25) => ("SE_TOTAL", 82),
        (false, 1) => ("VO_RESULT", 21),
        (false, 2) => ("VO_RESULT", 22),
        (false, 13) => ("VO_TOTAL", 48),
        _ => null,
    };

    private static bool call(LumenPlayer player, string name, params long[] arguments) =>
        player.TryInvokeCallback(name, [.. arguments.Select(static value => LumenHostValue.FromNumber(value))]);
}
