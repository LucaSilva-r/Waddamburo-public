using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>
/// The game's dan course card between songs (between.lm). Traced session30, at each song's start: its clip nextsong goes
/// to "start" (its sounds are its own) with the song's title drawn into next_song_name. Ours reads dan.song and song.title.
/// </summary>
public static class GameDanCard
{
    public static void Show(LumenPlayer between, LumenNativeSurfaceKey title)
    {
        ArgumentNullException.ThrowIfNull(between);
        if (between.UsesProvider)
            return;
        between.SetNativeFill("next_song_name", title);
        if (!between.TryGotoLabel("nextsong", "start"))
            Console.Error.WriteLine("between has no nextsong clip to start.");
    }
}
