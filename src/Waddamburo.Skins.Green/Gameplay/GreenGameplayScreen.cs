using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Gameplay;

/// <summary>The game's gameplay movies outside the lanes: song_info (or ai_song_info), the course's between card, AI battle's.</summary>
public sealed class GreenGameplayScreen(LumenGameSceneInstance scene, Action<string, int>? cue) : IGameplayScreen
{
    public LumenGameSceneInstance Scene => scene;

    // A movie of the scene by name; null when the scene has none.
    private LumenPlayer? movie(string name)
    {
        var index = scene.Layers.ToList().FindIndex(entry => Path.GetFileNameWithoutExtension(entry.Definition.MovieId) == name);
        return index < 0 ? null : scene.Player.Layers[index].Player;
    }

    // Both movies share the song_name, main/genre and main/stage slots.
    private LumenPlayer? songInfo => movie("song_info") ?? movie("ai_song_info");

    public void ShowMode(LumenNativeSurfaceKey? text)
    {
        if (songInfo is { } info)
            TaikoSongInfoPresentation.ShowMode(info, text);
    }

    public void ShowTitle(Func<LumenNativeSurfaceKey> title)
    {
        if (songInfo is { } info)
            TaikoSongInfoPresentation.SetTitle(info, title());
    }

    public void ShowNextSong(string genre, TaikoSongInfo info)
    {
        if (movie("song_info") is { } panel)
            TaikoSongInfoPresentation.ShowNextSong(panel, genre, info);
    }

    public void ShowCourseCard(LumenNativeSurfaceKey title)
    {
        if (movie("between") is { } between)
            GameDanCard.Show(between, title);
    }

    public void UpdateVisibility(bool showStage, bool showInfo)
    {
        if (songInfo is { } info)
            TaikoSongInfoPresentation.UpdateVisibility(info, showStage, showInfo);
    }

    public void StartAiBattle(AiBattleProgress battle) => _ = new GameAiBattleMovies(battle, scene, cue);
}
