using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>What song_info asks the host: genre index and stage (song number in the session).</summary>
/// <param name="Genre">song_info's genreArray index: jpop, anime, vocaloid, doyo, variety, classic, game, namco.</param>
/// <param name="Stage">1-based song number in the credit.</param>
/// <param name="Final">The credit's last song: the stage number shows red.</param>
public readonly record struct TaikoSongInfo(int Genre, int Stage, bool Final = false)
{
    /// <summary>song_info's stage frame: 1-4 white, 5-8 the same numbers red (final).</summary>
    public int StageFrame => Math.Clamp(Stage, 1, 4) + (Final ? 4 : 0);
}

/// <summary>Minimal gameplay host so authored enso movies can run their initialization.</summary>
public sealed class TaikoGameplayHostBinding(Func<TaikoSongInfo>? songInfo = null, Action<string, int>? playCue = null) : ILumenHostBinding
{
    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", static _ => { });
        // between, a dan course's next-song card: ExternalInterface CallSE(name, moment), traced SE_GAME 17, 19, 20.
        context.RegisterExternalInterfaceCall(call =>
        {
            if (call.Arguments.Length > 2 && call.Arguments[0].Kind == LumenHostValueKind.Text && call.Arguments[0].AsString() == "CallSE"
                && call.Arguments[2].Kind == LumenHostValueKind.Text)
                playCue?.Invoke("SE_GAME", call.Arguments[2].AsString() switch { "Start" => 17, "Close" => 19, _ => 20 });
            return LumenHostValue.Undefined;
        });
        // song_info: main.genre.gotoAndStop(genreArray[GetGenrNumber()]), main.stage.gotoAndStop(GetPlayCount()).
        context.RegisterObject("LumenSongInfo", info =>
        {
            info.RegisterMethod("GetGenrNumber", _ => LumenHostValue.FromNumber(songInfo?.Invoke().Genre ?? 0));
            info.RegisterMethod("GetPlayCount", _ => LumenHostValue.FromNumber(songInfo?.Invoke().StageFrame ?? 1));
        });
    }
}
