using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Audio;

/// <summary>The game's sounds from the user's sound tree: one shared bank, one cue map per scene.</summary>
internal sealed class GameSounds
{
    public GameSounds(AudioEngine audio, string soundRoot)
    {
        Bank = new SoundBank(audio, soundRoot);
        Attract = new AttractSounds(Bank);
        Frontend = new FrontendSounds(Bank);
        Gameplay = new GameplaySounds(Bank);
        Results = new ResultSounds(Bank);
        WaiwaiResults = new WaiwaiResultSounds(Bank);
        Retry = new RetrySounds(Bank, StopAll);
        GameOver = new GameOverSounds(Bank);
    }

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
        Frontend.Reset();
    }
}
