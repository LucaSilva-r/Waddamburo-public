using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.Gameplay;

/// <summary>Title and host-controlled visibility shared by the normal and AI battle song-info movies.</summary>
public static class TaikoSongInfoPresentation
{
    public static void SetTitle(LumenPlayer player, LumenNativeSurfaceKey title)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.SetNativeFill("song_name", title);
    }

    /// <summary>Apply after movie animation, so authored alpha changes or rebuilt clips cannot reveal hidden metadata.</summary>
    public static void UpdateVisibility(LumenPlayer player, bool showStage, bool showInfo)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.SetInstanceHidden("main/stage", !showStage);
        player.SetInstanceHidden("main", !showInfo);
        // Without the stage number (home, Issue #33) the board stays still: from frame 90 the movie cycles the title
        // out for the number and back, which there only fades it. Its first frame shows it whole.
        if (!showStage && player.InstanceFrame("main") is { Playing: true })
            player.TryGotoFrame("main", 0, play: false);
    }
}
