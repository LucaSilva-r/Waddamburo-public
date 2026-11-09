using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// AI battle's farewell at the end of a credit (ghost/remind's remind, traced session-ai-battle-3): at load
/// SetPlayerJoin(left, right), SetRankNumber(rank, at the top), SetNextWinPointNumber(points to the next rank),
/// SetNextRewardNumber(wins to the next reward, every reward had); the movie then waits for a don (PlaySound(1, 0) =
/// SE_COM 0) and ends (_global.isAllEnd).
/// </summary>
public sealed class AiRemindBinding(AiBattlePanels standing, Action<string, int>? playCue) : ILumenHostBinding
{
    // ponytail: rank 10 is the top reachable (10+ only through the 課題曲 folder, not kept).
    private const int TopRank = 10;

    private LumenPlayer? _movie;
    private bool _sent;

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var name in new[] { "LumenMethod", "Lumen" })
            context.RegisterObject(name, method =>
            {
                // ponytail: only the traced call type (1 = SE_COM); the movie's others (VO_COM, AI result banks) unseen.
                method.RegisterMethod("PlaySound", call =>
                {
                    if (call.Arguments.Length >= 2 && call.Arguments[0].Kind == LumenHostValueKind.Number
                        && call.Arguments[0].AsNumber() == 1 && call.Arguments[1].Kind == LumenHostValueKind.Number)
                        playCue?.Invoke("SE_COM", (int)call.Arguments[1].AsNumber());
                    return LumenHostValue.Undefined;
                });
                method.RegisterMethod("SetPlayerBasicData", static _ => LumenHostValue.Undefined);
            });
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _movie = player;
        Poll();
    }

    /// <summary>Each tick: the standing goes out once the movie has registered its callbacks (after its first frame).</summary>
    public void Poll()
    {
        if (_sent || _movie is not { CallbackNames.Length: > 0 } movie)
            return;
        _sent = true;
        call(movie, "SetPlayerJoin", LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(false));
        call(movie, "SetRankNumber", LumenHostValue.FromNumber(standing.Rank), LumenHostValue.FromBoolean(standing.Rank >= TopRank));
        call(movie, "SetNextWinPointNumber", LumenHostValue.FromNumber(standing.PointsToNextRank));
        call(movie, "SetNextRewardNumber", LumenHostValue.FromNumber(standing.WinsToNextReward),
            LumenHostValue.FromBoolean(standing.WinsToNextReward == 0));
    }

    /// <summary>The cabinet's own end (a timeout): the movie closes as if the player had hit the drum.</summary>
    public void Exit() => _movie?.TryInvokeCallback("SetExitSignal", []);

    private static void call(LumenPlayer movie, string name, params LumenHostValue[] arguments) => movie.TryInvokeCallback(name, arguments);
}
