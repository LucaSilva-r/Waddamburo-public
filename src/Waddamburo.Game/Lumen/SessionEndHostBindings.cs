using Waddamburo.Game.Don;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

/// <summary>
/// Arcade credit rules (user-described, traced in research/traces/session4-revival.md): a credit plays
/// a fixed number of songs; failing the first one offers the drum-roll revival (retry_game), whose
/// loss ends the credit; later failures just move on.
/// </summary>
public enum TaikoCreditNext { NextSong, Revival, End }

public static class TaikoCredit
{
    /// <summary>
    /// What follows a song's results, per player cleared (one or two players). One player: a failed
    /// first song earns the revival drum roll. Two players have no revival: the credit ends only when
    /// both failed the first song (user-confirmed cabinet rule). Otherwise the credit plays
    /// <paramref name="songsPerSession"/> songs (config.cfg; the traced cabinet used 2).
    /// </summary>
    public static TaikoCreditNext Next(int stage, IReadOnlyList<bool> cleared, int songsPerSession) =>
        stage == 1 && !cleared.Any(static value => value)
            ? cleared.Count == 1 ? TaikoCreditNext.Revival : TaikoCreditNext.End
            : stage >= songsPerSession ? TaikoCreditNext.End : TaikoCreditNext.NextSong;

    /// <summary>
    /// result.lm SetEndMessage: 2 revival; 1 ("another chance") when one of two players failed the
    /// first song (traced session9-2p; first song only, user-confirmed); otherwise 0 (traced after a
    /// cleared song and at the credit's end).
    /// </summary>
    public static int EndMessage(int stage, IReadOnlyList<bool> cleared, int songsPerSession) =>
        Next(stage, cleared, songsPerSession) switch
        {
            TaikoCreditNext.Revival => 2,
            TaikoCreditNext.NextSong when stage == 1 && cleared.Count == 2 && cleared.Contains(false) => 1,
            _ => 0,
        };
}

public enum RetrySoundRequestKind
{
    Effect,
    Voice,
}

public readonly record struct RetrySoundRequest(RetrySoundRequestKind Kind, int Group, int Cue);

public interface IRetrySoundController
{
    void RequestSound(RetrySoundRequest request);

    void StartMusic();

    void StopAll();
}

/// <summary>
/// retry_game.lm, the revival drum roll: the game sets the norma and the hands' colours (traced), the
/// movie counts hits itself, calls ExternalInterface "SetDonCameraSuccess" on a win and sets
/// _global.isAllEnd when its ending is over.
/// </summary>
public sealed class RetryGameHostBinding(
    IDonPresentationController? don = null,
    IRetrySoundController? sounds = null) : ILumenHostBinding
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
            if (name == "SetDonCameraSuccess")
            {
                Succeeded = true;
                don?.SetCameraLayout(DonPresentationLayout.RetrySuccess);
            }
            else if (name == "DonMot" && don is not null) applyRetryMotion(context, don, call.Arguments[1..]);
            return LumenHostValue.Undefined;
        });
        context.RegisterObject("LumenMethod", method =>
        {
            method.RegisterMethod("SE_REQUEST", call => requestSound(RetrySoundRequestKind.Effect, call));
            method.RegisterMethod("VOICE_REQUEST", call => requestSound(RetrySoundRequestKind.Voice, call));
            method.RegisterMethod("SOUND_STOP_ALL", _ =>
            {
                sounds?.StopAll();
                return LumenHostValue.Undefined;
            });
            method.RegisterMethod("BGM_START", _ =>
            {
                sounds?.StartMusic();
                return LumenHostValue.Undefined;
            });
            // Cabinet LEDs need their own scene-level integration.
            method.RegisterMethod("LED_SET", static _ => LumenHostValue.Undefined);
        });
    }

    private LumenHostValue requestSound(RetrySoundRequestKind kind, LumenHostCall call)
    {
        if (sounds is not null
            && tryInteger(call.Arguments, 0, out var group)
            && tryInteger(call.Arguments, 1, out var cue))
        {
            sounds.RequestSound(new RetrySoundRequest(kind, group, cue));
        }
        return LumenHostValue.Undefined;
    }

    private static bool tryInteger(
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments,
        int index,
        out int result)
    {
        result = 0;
        if ((uint)index >= (uint)arguments.Length || arguments[index].Kind != LumenHostValueKind.Number)
            return false;
        var number = arguments[index].AsNumber();
        if (!double.IsFinite(number) || number != Math.Truncate(number)
            || number < int.MinValue || number > int.MaxValue)
            return false;
        result = (int)number;
        return true;
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (don is not null)
        {
            DonLumenBinding.Attach(player, don, DonPresentationLayout.Retry);
            // The scene begins in this held pose before its first authored DonMot call.
            for (var index = 0; index < 2; index++)
                don.SetMotion(new DonMotionRequest(index, "don_fukkatu_face01", "don_fukkatu_face01"));
        }
        // ponytail: the right drum's revival is untraced; it keeps the traced P1 calls (the 2P status
        // draws mirrored hands) and the host shows Katsu-chan in the player's slot.
        LumenPlayerCalls.Call(player, "SetPlayerStatus", LumenHostValue.FromBoolean(true), LumenHostValue.FromBoolean(false));
        LumenPlayerCalls.Call(player, "SetNorma", LumenHostValue.FromNumber(Norma));
        LumenPlayerCalls.Call(player, "SetHandsColor", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(HandsColor));
    }

    // End() is a DefineFunction2 preloading _global into its first register: the flag lives there.
    // result.lm ends the same way.
    public static bool IsEnd(LumenPlayer player) => player.ReadScriptValue("_global.isAllEnd") is true;

    private static void applyRetryMotion(
        LumenHostContext context,
        IDonPresentationController don,
        IReadOnlyList<LumenHostValue> arguments)
    {
        if (arguments.Count < 3 || arguments[0].Kind != LumenHostValueKind.Number)
            return;
        var player = arguments[0].AsNumber();
        if (player is not (0 or 1))
            return;
        string? resolve(LumenHostValue value)
        {
            if (value.Kind != LumenHostValueKind.Number)
                return null;
            // The revival requests name the displayed pose. Their observed motion mapping
            // differs from the generic movie constant lookup for this scene.
            return value.AsNumber() switch
            {
                5 => "don_swing01",
                56 => "don_fukkatu_face01",
                57 => "don_fukkatu_face02",
                58 => "don_fukkatu_face03",
                59 => "don_fukkatu1P_success",
                60 => "don_fukkatu2P_success",
                _ => null,
            };
        }
        var oneShot = resolve(arguments[1]);
        var loop = resolve(arguments[2]);
        if (oneShot is not null || loop is not null)
            don.SetMotion(new DonMotionRequest((int)player, oneShot, loop));
        else
            DonLumenBinding.ApplyMotion(context, don, arguments);
    }
}

public enum GameOverSoundRequestKind { Effect, Voice }

public readonly record struct GameOverSoundRequest(GameOverSoundRequestKind Kind, int Group, int Cue);

public interface IGameOverSoundController
{
    void StartGameOverMusic();

    void StartGameOverOutro();

    void RequestSound(GameOverSoundRequest request);

    void StopGameOverMusic();
}

/// <summary>
/// shop_gameover.lm, the end-of-credit screen: the movie asks for the players (answered with
/// SetPlayerBasicData), waits for IsReady_Gameover and reports LumenTerminate_Gameover when done.
/// </summary>
public sealed class GameOverHostBinding(IGameOverSoundController? sounds = null, int side = 0, bool twoPlayers = false)
    : ILumenHostBinding
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
            lumen.RegisterMethod("RequestBGMGOver", _ =>
            {
                sounds?.StartGameOverOutro();
                return LumenHostValue.Undefined;
            });
            lumen.RegisterMethod("RequestSE", call => requestSound(GameOverSoundRequestKind.Effect, call));
            lumen.RegisterMethod("RequestVoice", call => requestSound(GameOverSoundRequestKind.Voice, call));
            lumen.RegisterMethod("RequestVO", call => requestSound(GameOverSoundRequestKind.Voice, call));
        });
    }

    public void Attach(LumenPlayer player)
    {
        _player = player;
        sounds?.StartGameOverMusic();
        if (_infoRequested) sendPlayers(player);
    }

    private LumenHostValue requestSound(GameOverSoundRequestKind kind, LumenHostCall call)
    {
        if (sounds is not null
            && call.Arguments.Length >= 2
            && integer(call.Arguments[0], out var group)
            && integer(call.Arguments[1], out var cue))
            sounds.RequestSound(new GameOverSoundRequest(kind, group, cue));
        return LumenHostValue.Undefined;
    }

    private static bool integer(LumenHostValue value, out int result)
    {
        result = 0;
        if (value.Kind != LumenHostValueKind.Number) return false;
        var number = value.AsNumber();
        if (!double.IsFinite(number) || number != Math.Truncate(number)
            || number < int.MinValue || number > int.MaxValue) return false;
        result = (int)number;
        return true;
    }

    // ponytail: guests (no card data) on their drums.
    private void sendPlayers(LumenPlayer player)
    {
        LumenPlayerCalls.Call(player, "SetPlayerBasicData", LumenHostValue.FromNumber(0),
            LumenHostValue.FromBoolean(twoPlayers || side == 0), LumenHostValue.FromBoolean(false));
        LumenPlayerCalls.Call(player, "SetPlayerBasicData", LumenHostValue.FromNumber(1),
            LumenHostValue.FromBoolean(twoPlayers || side == 1), LumenHostValue.FromBoolean(false));
    }
}
