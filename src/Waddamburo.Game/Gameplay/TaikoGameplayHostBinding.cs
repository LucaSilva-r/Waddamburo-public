using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>What song_info asks the host: genre index and stage (song number in the session).</summary>
/// <param name="Genre">song_info's genreArray index: jpop, anime, vocaloid, doyo, variety, classic, game, namco.</param>
/// <param name="Stage">1-based song number (the movie's stage frame).</param>
public readonly record struct TaikoSongInfo(int Genre, int Stage);

/// <summary>Minimal gameplay host so authored enso movies can run their initialization.</summary>
public sealed class TaikoGameplayHostBinding(Func<TaikoSongInfo>? songInfo = null) : ILumenHostBinding
{
    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", static _ => { });
        // song_info: main.genre.gotoAndStop(genreArray[GetGenrNumber()]), main.stage.gotoAndStop(GetPlayCount()).
        context.RegisterObject("LumenSongInfo", info =>
        {
            info.RegisterMethod("GetGenrNumber", _ => LumenHostValue.FromNumber(songInfo?.Invoke().Genre ?? 0));
            info.RegisterMethod("GetPlayCount", _ => LumenHostValue.FromNumber(songInfo?.Invoke().Stage ?? 1));
        });
    }
}
