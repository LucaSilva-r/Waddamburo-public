using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum ResultSoundRequestKind { Effect, Voice }

public readonly record struct ResultSoundRequest(
    ResultSoundRequestKind Kind, int Group, int Cue, bool Cleared, bool FullCombo);

public interface IResultSoundController
{
    void RequestSound(ResultSoundRequest request);
}

/// <summary>
/// The 1P results screen (enso_result/result.lm) as the game drives it (traced, research/traces):
/// the play's numbers and the player name at load; the movie then animates on its own, asking the
/// host for Don-chan's motions (ExternalInterface.call("DonMot", player, in, loop)) and sounds
/// (LumenMethod.SE_REQUEST / VOICE_REQUEST).
/// </summary>
public sealed class ResultHostBinding(
    Func<TaikoPlayResult> result,
    string playerName,
    int stage,
    int endMessage,
    IDonPresentationController? don = null,
    IResultSoundController? sounds = null) : ILumenHostBinding
{
    private LumenHostContext? _context;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        context.RegisterExternalInterfaceCall(call =>
        {
            if (call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Text
                && call.Arguments[0].AsString() == "DonMot" && don is not null)
                DonLumenBinding.ApplyMotion(context, don, call.Arguments[1..]);
            return LumenHostValue.Undefined;
        });
        context.RegisterObject("LumenMethod", method =>
        {
            method.RegisterMethod("SE_REQUEST", call => requestSound(ResultSoundRequestKind.Effect, call));
            method.RegisterMethod("VOICE_REQUEST", call => requestSound(ResultSoundRequestKind.Voice, call));
        });
    }

    private LumenHostValue requestSound(ResultSoundRequestKind kind, LumenHostCall call)
    {
        if (sounds is not null
            && tryInteger(call.Arguments, 0, out var group)
            && tryInteger(call.Arguments, 1, out var cue))
        {
            var play = result();
            sounds.RequestSound(new ResultSoundRequest(kind, group, cue, play.Cleared, play.FullCombo));
        }
        return LumenHostValue.Undefined;
    }

    private static bool tryInteger(
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments, int index, out int result)
    {
        result = 0;
        if ((uint)index >= (uint)arguments.Length || arguments[index].Kind != LumenHostValueKind.Number)
            return false;
        var value = arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value)
            || value < int.MinValue || value > int.MaxValue)
            return false;
        result = (int)value;
        return true;
    }

    /// <summary>The calls the game makes when the results load (P1, free-play guest).</summary>
    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (don is not null)
            DonLumenBinding.Attach(player, don);
        var play = result();
        var p = number(0);
        var no = LumenHostValue.FromBoolean(false);
        call(player, "SetRunMode", number(0));
        call(player, "SetSongPlayCount", number(stage));
        call(player, "SetPlayerStatus", LumenHostValue.FromBoolean(true), no);
        call(player, "SetEndMessage", number(endMessage));
        call(player, "SetCourse", p, number(play.CourseIndex));
        call(player, "SetScore", p, number(play.Score));
        call(player, "SetBestScore", p, number(-10)); // no previous best
        call(player, "SetRyo", p, number(play.Great));
        call(player, "SetKa", p, number(play.Good));
        call(player, "SetFuka", p, number(play.Miss));
        call(player, "SetCombo", p, number(play.MaxCombo));
        call(player, "SetRenda", p, number(play.Rolls));
        call(player, "SetGauge", p, number(play.GaugeSegments));
        call(player, "SetResultLevel", p, number(play.ResultLevel));
        call(player, "SetFullCombo", p, number(play.FullCombo ? 1 : 0));
        call(player, "SetOptionSpeed", p, number(1));
        call(player, "SetOptionDoron", p, no);
        call(player, "SetOptionAbekobe", p, no);
        call(player, "SetOptionRandomLevel", p, number(0));
        call(player, "SetOptionShinuchi", p, no);
        call(player, "SetPlayerForPlayerName", number(0), p);
        call(player, "SetTitleForPlayerName", LumenHostValue.FromString(""), p);
        call(player, "SetTitlePanelForPlayerName", number(0), p);
        call(player, "SetDaniForPlayerName", number(0), no, p);
        var characters = System.Globalization.StringInfo.GetTextElementEnumerator(playerName);
        call(player, "SetNameSizeForPlayerName", number(new System.Globalization.StringInfo(playerName).LengthInTextElements), p);
        for (var index = 0; characters.MoveNext(); index++)
            call(player, "SetCharForPlayerName", number(index), LumenHostValue.FromString(characters.GetTextElement()), p);
        call(player, "ApplyForPlayerName");
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Result movie is missing callback '{name}'.");
    }
}
