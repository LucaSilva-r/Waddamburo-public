using Waddamburo.Game.Sound;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Skins.Green.Sound;

namespace Waddamburo.App.Audio;

/// <summary>The game's sounds from the user's sound tree: one shared bank, one cue map per scene.</summary>
internal sealed class GameSounds
{
    public GameSounds(AudioEngine audio, string soundRoot)
    {
        var green = new GreenSoundSet(soundRoot);
        Set = green;
        Bank = new SoundBank(audio, green);
        Attract = new AttractSounds(Bank);
        Frontend = new FrontendSounds(Bank);
        Gameplay = new GameplaySounds(Bank, green);
        Results = new ResultSounds(Bank);
        WaiwaiResults = new WaiwaiResultSounds(Bank);
        Retry = new RetrySounds(Bank, StopAll);
        GameOver = new GameOverSounds(Bank);
    }

    /// <summary>The skin's sounds the engine asks for by name.</summary>
    public ISoundSet Set { get; }

    public SoundBank Bank { get; }
    public AttractSounds Attract { get; }
    public FrontendSounds Frontend { get; }
    public GameplaySounds Gameplay { get; }
    public ResultSounds Results { get; }
    public WaiwaiResultSounds WaiwaiResults { get; }
    public RetrySounds Retry { get; }
    public GameOverSounds GameOver { get; }

    /// <summary>Everything but the coin stops, and the next credit starts fresh.</summary>
    public void StopAll()
    {
        Bank.StopAll();
    }
}
