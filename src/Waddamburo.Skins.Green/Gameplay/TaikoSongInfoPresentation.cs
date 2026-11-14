using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>Title and host-controlled visibility shared by the normal and AI battle song-info movies.</summary>
public static class TaikoSongInfoPresentation
{
    /// <summary>
    /// song_info asks LumenSongInfo only as the song starts (main.genre.gotoAndStop(genreArray[GetGenrNumber()]),
    /// main.stage.gotoAndStop(GetPlayCount())), so a course's next songs set its clips the same way.
    /// </summary>
    public static void ShowNextSong(LumenPlayer player, string genreLabel, TaikoSongInfo info)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.UsesProvider) return; // ours reads song.genre and song.stage
        if (!(player.TryGotoLabel("main/genre", genreLabel, play: false)
                && player.TryGotoFrame("main/stage", info.StageFrame - 1, play: false)))
            Console.Error.WriteLine("song_info has no main.genre / main.stage clip to set.");
    }

    public static void SetTitle(LumenPlayer player, LumenNativeSurfaceKey title)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.UsesProvider) return; // ours reads song.title
        player.SetNativeFill("song_name", title);
    }

    /// <summary>Apply after movie animation, so authored alpha changes or rebuilt clips cannot reveal hidden metadata.</summary>
    public static void UpdateVisibility(LumenPlayer player, bool showStage, bool showInfo)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.UsesProvider) return; // ours: song.stage has no value when it is not shown
        player.SetInstanceHidden("main/stage", !showStage);
        player.SetInstanceHidden("main", !showInfo);
        // Without the stage number (home, Issue #33) the board stays still: from frame 90 the movie cycles the title
        // out for the number and back, which there only fades it. Its first frame shows it whole.
        if (!showStage && player.InstanceFrame("main") is { Playing: true })
            player.TryGotoFrame("main", 0, play: false);
    }
}
