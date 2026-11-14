using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Screens;

/// <summary>
/// The game's intermission movies (the rainbow, the fade, the dan covers, the shutters) under the engine's transition
/// protocol (transition.phase): the rainbow and covers by their in / out labels (in_extra / out_extra with the song's
/// title, which is drawn into scene_change_song_name), the shutters by Close(side). Ours read the protocol and are left
/// alone.
/// </summary>
public static class GameIntermissions
{
    /// <summary>The game's rainbow draws the song's title into this clip.</summary>
    public const string SongTitleFill = "scene_change_song_name";

    /// <summary>The game's rainbow: the song's title, drawn by the host (ours read song.title).</summary>
    public static void ShowTitle(LumenPlayer movie, Waddamburo.Lumen.Rendering.LumenNativeSurfaceKey title)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (!movie.UsesProvider) movie.SetNativeFill(SongTitleFill, title);
    }

    /// <summary>Covers the screen; false while the movie cannot take it yet (a shutter registers Close on its first frame).</summary>
    /// <param name="side">Whose colour a shutter takes: 0 the left drum, 1 the right, 2 both (traced Close(1), Close(2)).</param>
    public static bool Cover(LumenPlayer movie, bool title, int side)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (movie.UsesProvider)
            return true;
        if (movie.Labels.ContainsKey("in"))
        {
            movie.GotoLabel(title ? "in_extra" : "in", play: true);
            return true;
        }
        return movie.TryInvokeCallback("Close", [LumenHostValue.FromNumber(side)]);
    }

    /// <summary>Uncovers what is under the movie (the rainbow and the covers; the game's shutters open with the next screen).</summary>
    public static void Reveal(LumenPlayer movie, bool title)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (!movie.UsesProvider)
            movie.GotoLabel(title ? "out_extra" : "out", play: true);
    }
}
