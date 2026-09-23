using Waddamburo.Game.Don;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// Arcade credit rules (user-described, traced in research/traces/session4-revival.md): a credit plays
/// a fixed number of songs; failing the first one offers the drum-roll revival (retry_game), whose
/// loss ends the credit; later failures just move on.
/// </summary>
public static class TaikoCredit
{
    // ponytail: service-menu setting (the traced cabinet: 2 songs per credit); no option until settings exist.
    public const int SongsPerCredit = 2;

    /// <summary>result.lm SetEndMessage: 0 none (credit over), 1 one more song, 2 revival.</summary>
    public static int EndMessage(int stage, bool cleared) =>
        !cleared && stage == 1 ? 2 : stage >= SongsPerCredit ? 0 : 1;
}

/// <summary>
/// retry_game.lm, the revival drum roll: the game sets the norma and the hands' colours (traced), the
/// movie counts hits itself, calls ExternalInterface "SetDonCameraSuccess" on a win and sets
/// _global.isAllEnd when its ending is over.
/// </summary>
public sealed class RetryGameHostBinding(IDonPresentationController? don = null) : ILumenHostBinding
{
    // ponytail: the game sent 7, 100 and 40 across three traced revivals with no visible rule.
    private const int Norma = 40;
    private const int HandsColor = 16314592; // traced 0xF8F0E0 for P1

    public bool Succeeded { get; private set; }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterExternalInterfaceCall(call =>
        {
            var name = call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Text
                ? call.Arguments[0].AsString() : null;
            if (name == "SetDonCameraSuccess") Succeeded = true;
            else if (name == "DonMot" && don is not null) DonLumenBinding.ApplyMotion(context, don, call.Arguments[1..]);
            return LumenHostValue.Undefined;
        });
        // ponytail: sounds, BGM and cabinet LEDs come with the sound pass.
        context.RegisterObject("LumenMethod", method =>
        {
            foreach (var name in new[] { "SE_REQUEST", "VOICE_REQUEST", "LED_SET", "BGM_START", "SOUND_STOP_ALL" })
                method.RegisterMethod(name, static _ => LumenHostValue.Undefined);
        });
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (don is not null)
            DonLumenBinding.Attach(player, don);
        LumenPlayerCalls.Call(player, "SetPlayerStatus", LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(false));
        LumenPlayerCalls.Call(player, "SetNorma", LumenHostValue.FromNumber(Norma));
        LumenPlayerCalls.Call(player, "SetHandsColor", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(HandsColor));
    }

    // End() is a DefineFunction2 preloading _global into its first register: the flag lives there.
    public static bool IsEnd(LumenPlayer player) => player.ReadScriptValue("_global.isAllEnd") is true;
}

/// <summary>
/// shop_gameover.lm, the end-of-credit screen: the movie asks for the players (answered with
/// SetPlayerBasicData), waits for IsReady_Gameover and reports LumenTerminate_Gameover when done.
/// </summary>
public sealed class GameOverHostBinding : ILumenHostBinding
{
    private LumenPlayer? _player;
    private bool _infoRequested;

    public bool Ended { get; private set; }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("GetPlayerInfo_Gameover", _ =>
            {
                // Asked on the first frame, possibly before Attach hands over the player.
                _infoRequested = true;
                if (_player is { } player) sendPlayers(player);
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("IsReady_Gameover", static _ => LumenHostValue.FromNumber(1));
            lumen.RegisterMethod("LumenTerminate_Gameover", _ =>
            {
                Ended = true;
                return LumenHostValue.Undefined;
            });
            foreach (var name in new[] { "RequestBGMGOver", "RequestSE", "RequestVO" })
                lumen.RegisterMethod(name, static _ => LumenHostValue.Undefined);
        });
    }

    public void Attach(LumenPlayer player)
    {
        _player = player;
        if (_infoRequested) sendPlayers(player);
    }

    // ponytail: P1 guest (no card data), P2 not joined.
    private static void sendPlayers(LumenPlayer player)
    {
        LumenPlayerCalls.Call(player, "SetPlayerBasicData", LumenHostValue.FromNumber(0),
            LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(false));
        LumenPlayerCalls.Call(player, "SetPlayerBasicData", LumenHostValue.FromNumber(1),
            LumenHostValue.FromBoolean(false), LumenHostValue.FromBoolean(false));
    }
}
