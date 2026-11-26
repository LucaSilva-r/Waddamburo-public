using Waddamburo.Game.Sound;

namespace Waddamburo.Skins.Green.Sound;

/// <summary>Game over: the card-recommendation loop, the game-over outro, and its two cues.</summary>
public sealed class GameOverSounds(GreenSounds bank) : IGameOverSoundController
{
    private SoundHandle? _music;
    private string? _musicName;

    public void StartGameOverMusic() => start("JINGLE_CARDRECOM", loop: true);

    public void StartGameOverOutro() => start("JINGLE_GOVER", loop: false);

    /// <summary>ごほうびタイム's music, in the same slot: the outro replaces it (traced).</summary>
    public void StartMedalMusic() => start("JINGLE_SOGO2", loop: true);

    public void StopGameOverMusic() => StopGameOverMusic(null);

    public void StopGameOverMusic(TimeSpan? fade)
    {
        bank.Stop(_music, fade);
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
                bank.Play("VO_GAMEOVER", 0);
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
        _music = bank.PlayMusic(name, loop);
        if (_music is not null)
            _musicName = name;
    }
}
