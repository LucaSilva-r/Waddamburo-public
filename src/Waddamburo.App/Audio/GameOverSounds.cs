using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>Game over: the card-recommendation loop, the game-over outro, and its two cues.</summary>
internal sealed class GameOverSounds(SoundBank bank) : IGameOverSoundController
{
    private AudioPlaybackHandle? _music;
    private string? _musicName;

    public void StartGameOverMusic() => start("JINGLE_CARDRECOM", loop: true);

    public void StartGameOverOutro() => start("JINGLE_GOVER", loop: false);

    public void StopGameOverMusic()
    {
        bank.Stop(_music);
        _music = null;
        _musicName = null;
    }

    public void RequestSound(GameOverSoundRequest request)
    {
        switch (request.Kind, request.Group, request.Cue)
        {
            case (GameOverSoundRequestKind.Effect, 0, 100):
                bank.Play("SE_GAMEOVER", 0);
                break;
            case (GameOverSoundRequestKind.Voice, 0, 100):
                bank.Play("VO_HOWTOPLAY", 0);
                break;
            default:
                Console.WriteLine($"GameOver.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
                break;
        }
    }

    private void start(string name, bool loop)
    {
        if (_musicName == name && bank.IsPlaying(_music))
            return;
        StopGameOverMusic();
        _music = bank.PlayMusic("Game Over", name, loop);
        if (_music is not null)
            _musicName = name;
    }
}
