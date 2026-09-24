using Waddamburo.Game.Don;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum ResultSoundRequestKind { Effect, Voice }

/// <summary>A results sound request; Cleared / FullCombo are the requesting player's (any player's for
/// shared group 0). TwoPlayers: the movie runs its two-player presentation.</summary>
public readonly record struct ResultSoundRequest(
    ResultSoundRequestKind Kind, int Group, int Cue, bool Cleared, bool FullCombo, bool TwoPlayers = false);

public interface IResultSoundController
{
    void RequestSound(ResultSoundRequest request);
}

/// <summary>
/// The results screen (enso_result/result.lm) as the game drives it (traced, research/traces): each
/// player's numbers and name board at load; the movie then animates on its own, asking the host for
/// Don-chan's motions (ExternalInterface.call("DonMot", player, in, loop)) and sounds
/// (LumenMethod.SE_REQUEST / VOICE_REQUEST, group 1/2 = the left/right player, 0 = both).
/// </summary>
/// <param name="results">One play (the player on <paramref name="side"/>), or two (left, right).</param>
public sealed class ResultHostBinding(
    Func<IReadOnlyList<TaikoPlayResult>> results,
    int stage,
    int endMessage,
    IDonPresentationController? don = null,
    IResultSoundController? sounds = null,
    int side = 0) : ILumenHostBinding
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
            var plays = results();
            var own = group is 1 or 2 && plays.Count == 2 ? [plays[group - 1]] : plays;
            sounds.RequestSound(new ResultSoundRequest(kind, group, cue, own.Any(play => play.Cleared),
                own.Any(play => play.FullCombo), plays.Count == 2));
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

    /// <summary>
    /// The calls the game makes when the results load (guest players). <c>side</c> 1 = the right
    /// drum's player alone (traced session8-p2-solo): its status flag and player index, and an end
    /// message of 0 whatever follows. Two players (session9-2p): both flags, each player's numbers,
    /// then each name board (board = player).
    /// </summary>
    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (don is not null)
            DonLumenBinding.Attach(player, don);
        var plays = results();
        var two = plays.Count == 2;
        call(player, "SetRunMode", number(0));
        call(player, "SetSongPlayCount", number(stage));
        call(player, "SetPlayerStatus", LumenHostValue.FromBoolean(two || side == 0), LumenHostValue.FromBoolean(two || side == 1));
        call(player, "SetEndMessage", number(endMessage));
        for (var index = 0; index < plays.Count; index++)
            sendPlay(player, plays[index], two ? index : side);
        for (var index = 0; index < plays.Count; index++)
            sendName(player, two ? index : side, boardIndex: two ? index : 0);
    }

    private static void sendPlay(LumenPlayer player, TaikoPlayResult play, int side)
    {
        var p = number(side);
        var no = LumenHostValue.FromBoolean(false);
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
    }

    // The name board calls end with the board index (0 for one player), not the player.
    private static void sendName(LumenPlayer player, int side, int boardIndex)
    {
        var playerName = TaikoGuest.Name(side);
        var board = number(boardIndex);
        var p = number(side);
        var no = LumenHostValue.FromBoolean(false);
        call(player, "SetPlayerForPlayerName", p, board);
        call(player, "SetTitleForPlayerName", LumenHostValue.FromString(""), board);
        call(player, "SetTitlePanelForPlayerName", number(0), board);
        call(player, "SetDaniForPlayerName", number(0), no, board);
        var characters = System.Globalization.StringInfo.GetTextElementEnumerator(playerName);
        call(player, "SetNameSizeForPlayerName", number(new System.Globalization.StringInfo(playerName).LengthInTextElements), board);
        for (var index = 0; characters.MoveNext(); index++)
            call(player, "SetCharForPlayerName", number(index), LumenHostValue.FromString(characters.GetTextElement()), board);
        call(player, "ApplyForPlayerName");
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Result movie is missing callback '{name}'.");
    }
}
